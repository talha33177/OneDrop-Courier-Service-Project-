using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Application.Payments.OnlinePayments;
using Domain.Parcels;
using Domain.Payments;
using Infrastructure.Payments;

namespace Integration.Tests;

/// <summary>
/// A recipient pays a parcel's cash on delivery online (the fake gateway here): the amount shows only to the phone the
/// parcel is for, the rider then collects nothing, the merchant is still paid the whole amount, and money for a parcel
/// that never arrives, or that the parcel can no longer take, goes back to the payer by itself.
/// </summary>
public partial class RecipientPaymentsTests(WebAppFactory factory) : AppTests(factory)
{
    [Fact]
    public async Task A_recipient_pays_online_the_rider_collects_nothing_and_the_merchant_is_paid_in_full()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: "Banani");
        var phone = NewPhone();
        var code = await BookAsync(shop.ApiKey, area: "Gulshan 2", cod: 1250, phone: phone);
        var recipient = Visit("onedrop");

        // Tracking offers it without naming the amount; the pay page names it only for the parcel's own phone
        var tracking = await recipient.PageAsync($"/Track?code={code}");
        Assert.Contains("Pay online", tracking);
        Assert.DoesNotContain("1,250", tracking);
        Assert.DoesNotContain("1,250", await recipient.PageAsync($"/Track/Pay/{code}"));
        Assert.Contains("could not find a parcel", await recipient.SubmitAsync($"/Track/Pay/{code}", $"/Track/Pay/{code}?handler=Check", ("Phone", NewPhone())));
        Assert.Contains("৳1,250", await recipient.SubmitAsync($"/Track/Pay/{code}", $"/Track/Pay/{code}?handler=Check", ("Phone", phone)));

        // Paying sends the recipient to the gateway for the parcel's own amount, and back to the parcel
        var (transaction, payment) = await StartAsync(recipient, code, phone);
        Assert.Equal((1250m, OnlinePaymentStatus.Started, shop.Id), (payment.Amount, payment.Status, payment.MerchantId));
        var validationId = Gateway.Pay(transaction, "BKASH-BKash")!;
        var back = await ReturnAsync(transaction, "success", ("val_id", validationId));
        Assert.Equal($"/Track/Pay/{code}?payment={payment.Number}", back.Headers.Location!.OriginalString);
        Assert.Contains("Paid. Thank you", await recipient.PageAsync(back.Headers.Location!.OriginalString));
        Assert.Equal((OnlinePaymentStatus.Paid, (long?)null), ((await PaymentAsync(transaction)).Status, (await PaymentAsync(transaction)).LedgerEntryId));
        Assert.Equal(1250m, (await ParcelAsync(code)).PaidOnline);
        Assert.Contains("Paid online.", await recipient.PageAsync($"/Track?code={code}"));

        // The browser coming back again, or a second payment, is never taken twice: the second goes back
        await ReturnAsync(transaction, "success", ("val_id", validationId));
        Assert.Contains("Already paid", await recipient.SubmitAsync($"/Track/Pay/{code}", $"/Track/Pay/{code}?handler=Check", ("Phone", phone)));

        // The merchant sees it on the parcel, not among its own payments, and cannot ask to change the cash any more
        var owner = await SignInAsync("onedrop", shop.Email);
        Assert.Contains("paid online by the recipient", await owner.PageAsync($"/Merchant/Parcel/{code}"));
        Assert.DoesNotContain(payment.Number, await owner.PageAsync("/Merchant/Payments"));

        // At the door the rider collects nothing; the merchant is paid the whole cash on delivery
        var rider = await NewRiderAsync("GUL");
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        await hub.SubmitAsync("/Hub/Scan?hub=GUL", "/Hub/Scan?mode=Receive&hub=GUL", ("Code", code));
        await hub.SubmitAsync("/Hub/Assign?hub=GUL", "/Hub/Assign?hub=GUL", ("riderId", $"{rider.Id}"), ("codes", code));
        var door = await SignInAsync("onedrop", rider.Email);
        Assert.Contains("already paid", await door.PageAsync($"/Rider/Delivery/{code}"));
        await door.SubmitAsync($"/Rider/Delivery/{code}", $"/Rider/Delivery/{code}?handler=Deliver", ("collected", "0"), ("reason", ""));

        var delivered = await ParcelAsync(code);
        Assert.Equal((ParcelStatus.Delivered, (decimal?)1250m, (decimal?)13m), (delivered.Status, delivered.CollectedAmount, delivered.CodCharge));
        Assert.Equal(1250m, await QueryAsync("onedrop", db => db.LedgerEntries.Where(e => e.ParcelId == delivered.Id && e.Kind == LedgerEntryKind.Cod).SumAsync(e => e.Amount, Cancel)));
        Assert.Equal(0m, await QueryAsync("onedrop", db => db.DeliveryAttempts.Where(a => a.ParcelId == delivered.Id).SumAsync(a => a.CollectedAmount, Cancel)));

        // Another courier's site finds no such parcel
        var rival = Visit("rival");
        Assert.Contains("could not find a parcel", await rival.SubmitAsync($"/Track/Pay/{code}", $"/Track/Pay/{code}?handler=Check", ("Phone", phone)));
    }

    [Fact]
    public async Task Money_for_a_parcel_that_is_cancelled_or_paid_twice_is_refunded_by_the_check()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: "Banani");
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");

        // Paid while booked, then the merchant cancels it: the check owes it back and refunds it through the gateway
        var phone = NewPhone();
        var cancelled = await BookAsync(shop.ApiKey, area: "Gulshan 2", cod: 800, phone: phone);
        var (paidThenCancelled, first) = await StartAsync(Visit("onedrop"), cancelled, phone);
        await ReturnAsync(paidThenCancelled, "success", ("val_id", Gateway.Pay(paidThenCancelled, "NAGAD-Nagad")!));
        Assert.Equal(800m, (await ParcelAsync(cancelled)).PaidOnline);
        Assert.Equal(HttpStatusCode.NoContent, (await Factory.ClientFor(shop.ApiKey).PostAsync($"/api/v1/parcels/{cancelled}/cancel", null, Cancel)).StatusCode);

        await CheckAsync();
        var refunded = await PaymentAsync(paidThenCancelled);
        Assert.Equal(OnlinePaymentStatus.Refunded, refunded.Status);
        Assert.Single(Gateway.Refunds, r => r.Reference == first.Number && r.Amount == 800m);

        // Two payments started for one parcel: the first is taken, the second goes back to the payer at once
        var twicePhone = NewPhone();
        var twice = await BookAsync(shop.ApiKey, area: "Gulshan 2", cod: 600, phone: twicePhone);
        var (one, _) = await StartAsync(Visit("onedrop"), twice, twicePhone);
        var (two, second) = await StartAsync(Visit("onedrop"), twice, twicePhone);
        await ReturnAsync(one, "success", ("val_id", Gateway.Pay(one, "BKASH-BKash")!));
        await ReturnAsync(two, "success", ("val_id", Gateway.Pay(two, "BKASH-BKash")!));
        Assert.Equal((OnlinePaymentStatus.Paid, OnlinePaymentStatus.RefundDue), ((await PaymentAsync(one)).Status, (await PaymentAsync(two)).Status));
        Assert.Equal(600m, (await ParcelAsync(twice)).PaidOnline);
        Assert.Contains("Refunds owed to recipients", await admin.PageAsync("/Admin/Payouts"));

        await CheckAsync();
        Assert.Equal(OnlinePaymentStatus.Refunded, (await PaymentAsync(two)).Status);
        Assert.Single(Gateway.Refunds, r => r.Reference == second.Number);
        Assert.Equal(OnlinePaymentStatus.Paid, (await PaymentAsync(one)).Status);

        // Checking again refunds nothing more
        await CheckAsync();
        Assert.Single(Gateway.Refunds, r => r.Reference == second.Number);
        Assert.Contains(second.Number, await admin.PageAsync("/Admin/Payouts"));
    }

    private FakePaymentLog Gateway => Factory.Services.GetRequiredService<FakePaymentLog>();

    /// <summary>The recipient gives their phone and email and is sent to the gateway's page.</summary>
    private async Task<(string Transaction, OnlinePayment Payment)> StartAsync(Visitor recipient, string code, string phone)
    {
        var started = await recipient.PostFormAsync(
            $"/Track/Pay/{code}",
            $"/Track/Pay/{code}?handler=Start",
            ("Phone", phone),
            ("Email", "rahim@example.com"));
        Assert.Equal(HttpStatusCode.Redirect, started.StatusCode);
        var transaction = PaymentPage().Match(started.Headers.Location!.ToString()).Groups[1].Value;

        return (transaction, await PaymentAsync(transaction));
    }

    private Task<HttpResponseMessage> ReturnAsync(string transaction, string outcome, params (string Name, string Value)[] fields)
    {
        return Visit("onedrop").SendAsync(
            HttpMethod.Post,
            $"/pay/{transaction}/{outcome}",
            new FormUrlEncodedContent(fields.Select(field => KeyValuePair.Create(field.Name, field.Value))));
    }

    private Task<OnlinePayment> PaymentAsync(string transaction)
    {
        return QueryAsync("onedrop", db => db.OnlinePayments.AsNoTracking().SingleAsync(p => p.TransactionId == transaction, Cancel));
    }

    private async Task CheckAsync()
    {
        await using var scope = await ScopeAsync("onedrop");
        await ActivatorUtilities.CreateInstance<OnlinePaymentsHandler>(scope.ServiceProvider, (TimeProvider)new FakeTimeProvider(DateTime.UtcNow)).CheckAsync(Cancel);
    }

    [GeneratedRegex("/Dev/Pay/([0-9a-f]+)$")]
    private static partial Regex PaymentPage();
}
