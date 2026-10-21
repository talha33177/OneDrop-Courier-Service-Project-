using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Application.Payments.AdminPayouts;
using Application.Payments.RunPayouts;
using Domain.Merchants;
using Domain.Parcels;
using Domain.Payments;
using Infrastructure.Payments;
using Infrastructure.Persistence;

namespace Integration.Tests;

/// <summary>Next-day payouts: each merchant paid its cash less its charges, once, with charges carried forward.</summary>
public class PayoutTests(WebAppFactory factory) : AppTests(factory)
{
    [Fact]
    public async Task Yesterdays_lines_are_paid_once_and_a_merchant_whose_charges_are_more_waits()
    {
        WebAppFactory.RequireDatabase();
        var paid = await NewMerchantAsync();
        var owing = await NewMerchantAsync();
        var delivered = await DeliveredAsync(paid, cod: 1000);
        var returned = await ReturnedAsync(owing);
        var tomorrow = DateTime.UtcNow.AddDays(1);

        // While today lasts nothing is paid; the next day the job pays the merchant its ৳1,000 less ৳70
        var today = await RunAsync(DateTime.UtcNow);
        Assert.DoesNotContain(today.Payouts, p => p.MerchantId == paid.Id);
        await RunAsync(tomorrow);
        await RunAsync(tomorrow.AddMinutes(30));

        var payouts = await QueryAsync("onedrop", db => db.Payouts.Where(p => p.MerchantId == paid.Id).ToListAsync(Cancel));
        var payout = Assert.Single(payouts);
        Assert.Equal((1000m, 70m, 930m, PayoutStatus.Paid), (payout.CodTotal, payout.ChargesTotal, payout.Amount, payout.Status));
        Assert.Matches("^INV-[0-9]{6}$", payout.Number);
        var sent = Factory.Services.GetRequiredService<FakePayoutLog>().Recent.Where(p => p.Key == $"onedrop-payout-{payout.Id}").ToList();
        Assert.Equal(930, Assert.Single(sent).Amount);

        // The merchant who owes the return charges is paid nothing; its lines wait
        Assert.Empty(await QueryAsync("onedrop", db => db.Payouts.Where(p => p.MerchantId == owing.Id).ToListAsync(Cancel)));
        Assert.All(
            await QueryAsync("onedrop", db => db.LedgerEntries.Where(e => e.ParcelId == returned).ToListAsync(Cancel)),
            line => Assert.Null(line.PayoutId));

        // The merchant sees its invoice and its parcel on it; another merchant gets a 404
        var merchant = await SignInAsync("onedrop", paid.Email);
        var invoice = await merchant.PageAsync($"/Merchant/Payment/{payout.Number}");
        Assert.Contains(delivered, invoice);
        Assert.Contains("৳930", invoice);
        var other = await SignInAsync("onedrop", owing.Email);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/Merchant/Payment/{payout.Number}")).StatusCode);
        Assert.Contains(payout.Number, await (await SignInAsync("onedrop", "admin@onedrop.test")).PageAsync("/Admin/Payouts"));
    }

    [Fact]
    public async Task Lines_from_today_are_owed_but_payable_only_from_tomorrow()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        await DeliveredAsync(shop, cod: 1000);

        var today = await OwedAsync(shop.Id, DateTime.UtcNow);
        Assert.Equal(0, today.Payable);
        Assert.True(today.DueTomorrow > 0);

        var tomorrow = await OwedAsync(shop.Id, DateTime.UtcNow.AddDays(1));
        Assert.Equal(today.Net, tomorrow.Payable);
        Assert.Equal(0, tomorrow.DueTomorrow);
    }

    [Fact]
    public async Task A_refused_transfer_shows_why_and_is_sent_again_once_the_gateway_takes_it()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        await DeliveredAsync(shop, cod: 1000);
        var account = await QueryAsync("onedrop", db => db.Merchants.Where(m => m.Id == shop.Id).Select(m => m.PayoutAccount!).SingleAsync(Cancel));
        var gateway = Factory.Services.GetRequiredService<FakePayoutLog>();
        gateway.Refuse(account, "The wallet is frozen.");
        try
        {
            var admin = await SignInAsync("onedrop", "admin@onedrop.test");
            var merchantPage = $"/Admin/Merchant/{shop.Id}";
            Assert.Contains("the gateway refused it", await admin.SubmitAsync(merchantPage, $"{merchantPage}?handler=PayNow"));
            var payout = await QueryAsync("onedrop", db => db.Payouts.SingleAsync(p => p.MerchantId == shop.Id, Cancel));
            // Another test's run may have tried it again meanwhile, so it was refused at least once
            Assert.Equal((PayoutStatus.Pending, "The wallet is frozen."), (payout.Status, payout.LastError));
            Assert.True(payout.FailedAttempts >= 1);

            // The admin sees it and why; so does the merchant on its invoice
            var payouts = await admin.PageAsync("/Admin/Payouts");
            Assert.Contains("Refused by the gateway", payouts);
            Assert.Contains("The wallet is frozen.", payouts);
            var merchant = await SignInAsync("onedrop", shop.Email);
            Assert.Contains("The wallet is frozen.", await merchant.PageAsync($"/Merchant/Payment/{payout.Number}"));

            // Sent again while still refused, it says so and counts the try
            Assert.Contains("refused", await admin.SubmitAsync("/Admin/Payouts", "/Admin/Payouts?handler=Send", ("number", payout.Number)));
            gateway.StopRefusing(account);
            Assert.Matches("was sent|not waiting", await admin.SubmitAsync("/Admin/Payouts", "/Admin/Payouts?handler=Send", ("number", payout.Number)));

            var sent = await QueryAsync("onedrop", db => db.Payouts.SingleAsync(p => p.Id == payout.Id, Cancel));
            Assert.Equal((PayoutStatus.Paid, (string?)null), (sent.Status, sent.LastError));
            Assert.True(sent.FailedAttempts >= 2);
            Assert.Single(gateway.Recent, p => p.Key == $"onedrop-payout-{payout.Id}");
        }
        finally
        {
            gateway.StopRefusing(account);
        }
    }

    [Fact]
    public async Task A_cancelled_payout_frees_its_lines_for_a_payout_to_the_corrected_account()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        await DeliveredAsync(shop, cod: 1000);
        var wrong = await QueryAsync("onedrop", db => db.Merchants.Where(m => m.Id == shop.Id).Select(m => m.PayoutAccount!).SingleAsync(Cancel));
        var gateway = Factory.Services.GetRequiredService<FakePayoutLog>();
        gateway.Refuse(wrong, "No such wallet.");
        try
        {
            var admin = await SignInAsync("onedrop", "admin@onedrop.test");
            var merchantPage = $"/Admin/Merchant/{shop.Id}";
            await admin.SubmitAsync(merchantPage, $"{merchantPage}?handler=PayNow");
            var stuck = await QueryAsync("onedrop", db => db.Payouts.SingleAsync(p => p.MerchantId == shop.Id, Cancel));

            // Another courier's admin cannot touch it
            var rival = await SignInAsync("rival", "admin@rival.test");
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await rival.PostFormAsync("/Admin/Payouts", $"/Admin/Payout/{stuck.Number}?handler=Cancel")).StatusCode);

            // The account is corrected; the stuck payout still names the old one, so it is cancelled
            var right = NewPhone();
            await admin.SubmitAsync(merchantPage, $"{merchantPage}?handler=Payout", ("method", "Nagad"), ("payoutAccount", right), ("accountName", "Test Owner"));
            var invoice = $"/Admin/Payout/{stuck.Number}";
            Assert.Contains("was cancelled", await admin.SubmitAsync(invoice, $"{invoice}?handler=Cancel"));
            Assert.Equal(PayoutStatus.Cancelled, (await QueryAsync("onedrop", db => db.Payouts.SingleAsync(p => p.Id == stuck.Id, Cancel))).Status);
            Assert.Empty(await QueryAsync("onedrop", db => db.LedgerEntries.Where(e => e.PayoutId == stuck.Id).ToListAsync(Cancel)));

            // The next payout goes to the corrected account with the same money (made here, or by another test's run)
            await admin.SubmitAsync(merchantPage, $"{merchantPage}?handler=PayNow");
            var paid = await QueryAsync("onedrop", db => db.Payouts.SingleAsync(p => p.MerchantId == shop.Id && p.Status == PayoutStatus.Paid, Cancel));
            Assert.Equal((stuck.Amount, PayoutMethod.Nagad, "+88" + right), (paid.Amount, paid.Method, paid.Account));
        }
        finally
        {
            gateway.StopRefusing(wrong);
        }
    }

    [Fact]
    public async Task Held_payouts_wait_and_adjustments_settle_a_merchant_who_owes_the_courier()
    {
        WebAppFactory.RequireDatabase();
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");

        // A merchant with only a return owes the courier, and is listed apart until an adjustment settles it
        var owing = await NewMerchantAsync();
        await ReturnedAsync(owing);
        var owes = -await QueryAsync("onedrop", db => db.LedgerEntries.Where(e => e.MerchantId == owing.Id).SumAsync(e => e.Amount, Cancel));
        Assert.Contains("Owe the courier", await admin.PageAsync("/Admin/Payouts"));
        Assert.Contains((await OverviewAsync(DateTime.UtcNow)).OwingCourier, m => m.MerchantId == owing.Id && m.Net == -owes);
        var owingPage = $"/Admin/Merchant/{owing.Id}";
        await admin.SubmitAsync(owingPage, $"{owingPage}?handler=Adjust", ("direction", "credit"), ("amount", owes.ToString(CultureInfo.InvariantCulture)), ("note", "Paid at Mirpur hub"));
        Assert.DoesNotContain((await OverviewAsync(DateTime.UtcNow)).Owed, m => m.MerchantId == owing.Id);

        // Held, the merchant is not paid by the run nor when it asks, and reads why
        var shop = await NewMerchantAsync();
        await DeliveredAsync(shop, cod: 1000);
        var shopPage = $"/Admin/Merchant/{shop.Id}";
        Assert.Contains("Payouts held", await admin.SubmitAsync(shopPage, $"{shopPage}?handler=Hold", ("reason", "Checking a damage claim")));
        var merchant = await SignInAsync("onedrop", shop.Email);
        var payments = await merchant.PageAsync("/Merchant/Payments");
        Assert.Contains("Checking a damage claim", payments);
        Assert.DoesNotContain("Ask to be paid now", payments);
        Assert.Contains("on hold", await merchant.SubmitAsync("/Merchant/Payments", "/Merchant/Payments?handler=Ask"));
        await RunAsync(DateTime.UtcNow.AddDays(1));
        Assert.Empty(await QueryAsync("onedrop", db => db.Payouts.Where(p => p.MerchantId == shop.Id).ToListAsync(Cancel)));

        // With a charge written on it and released, it is paid the rest (here, or by another test's run) and reads why
        await admin.SubmitAsync(shopPage, $"{shopPage}?handler=Adjust", ("direction", "charge"), ("amount", "30"), ("note", "Extra packing"));
        await admin.SubmitAsync(shopPage, $"{shopPage}?handler=Release");
        await admin.SubmitAsync(shopPage, $"{shopPage}?handler=PayNow");
        var payout = await QueryAsync("onedrop", db => db.Payouts.SingleAsync(p => p.MerchantId == shop.Id, Cancel));
        Assert.Equal(1000 - 70 - 30, payout.Amount);
        Assert.Equal((70m, -30m), (payout.ChargesTotal, payout.AdjustmentsTotal));
        var invoice = await merchant.PageAsync($"/Merchant/Payment/{payout.Number}");
        Assert.Contains("Extra packing", invoice);
    }

    private async Task<MerchantBalance> OwedAsync(long merchantId, DateTime utcNow)
    {
        return (await OverviewAsync(utcNow)).Owed.Single(m => m.MerchantId == merchantId);
    }

    private async Task<PayoutsOverview> OverviewAsync(DateTime utcNow)
    {
        await using var scope = await ScopeAsync("onedrop");
        var handler = ActivatorUtilities.CreateInstance<AdminPayoutsHandler>(scope.ServiceProvider, (TimeProvider)new FakeTimeProvider(utcNow));

        return await handler.GetAsync(null, cancellationToken: Cancel);
    }

    private async Task<(PayoutRun Run, IReadOnlyList<Payout> Payouts)> RunAsync(DateTime utcNow)
    {
        await using var scope = await ScopeAsync("onedrop");
        var job = ActivatorUtilities.CreateInstance<PayoutsJob>(scope.ServiceProvider, (TimeProvider)new FakeTimeProvider(utcNow));
        var run = await job.PayAsync(Cancel);

        return (run, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Payouts.ToListAsync(Cancel));
    }

    private async Task<string> DeliveredAsync(TestMerchant shop, decimal cod)
    {
        var code = await BookAsync(shop.ApiKey, area: "Pallabi", cod: cod);
        await FinishAsync(code, (parcel, rider) =>
        {
            parcel.AssignTo(rider, parcel.CurrentHubId!.Value);

            return parcel.Deliver(cod, null, DateTime.UtcNow);
        });

        return code;
    }

    private async Task<long> ReturnedAsync(TestMerchant shop)
    {
        var code = await BookAsync(shop.ApiKey, area: "Pallabi", cod: 500);

        return await FinishAsync(code, (parcel, _) =>
        {
            parcel.RequestReturn("Merchant asked");

            return parcel.ReturnToMerchant(parcel.CurrentHubId!.Value, DateTime.UtcNow);
        });
    }

    /// <summary>Scans a parcel in at its hub and takes it through <paramref name="finish"/>, writing its ledger lines dated today.</summary>
    private async Task<long> FinishAsync(string code, Func<Parcel, long, Domain.Common.Result> finish)
    {
        await using var scope = await ScopeAsync("onedrop");
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var parcel = await db.Parcels.SingleAsync(p => p.TrackingCode == code, Cancel);
        var rider = await db.Riders.Where(r => r.HubId == parcel.PickupHubId).Select(r => r.Id).FirstAsync(Cancel);
        parcel.ReceiveAt(parcel.PickupHubId, Today);
        Assert.True(finish(parcel, rider).IsSuccess);

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Dhaka")));
        db.LedgerEntries.AddRange(LedgerEntry.For(parcel, today));
        await db.SaveChangesAsync(Cancel);

        return parcel.Id;
    }
}
