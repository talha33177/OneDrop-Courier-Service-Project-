using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Application.Payments.OnlinePayments;
using Domain.Payments;
using Infrastructure.Payments;

namespace Integration.Tests;

/// <summary>
/// A merchant who owes the courier pays online through the gateway (the fake one here): credited once the gateway
/// confirms it and never twice, never on the browser's word, still credited when the browser never comes back, and held
/// for the courier's admin when the gateway marks it risky.
/// </summary>
public partial class OnlinePaymentsTests(WebAppFactory factory) : AppTests(factory)
{
    [Fact]
    public async Task A_merchant_who_owes_pays_online_and_is_credited_once()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        var owner = await SignInAsync("onedrop", shop.Email);

        // Owing nothing, there is nothing to pay
        Assert.DoesNotContain("online</button>", await owner.PageAsync("/Merchant/Payments"));
        Assert.Contains("You owe the courier nothing", await owner.SubmitAsync("/Merchant/Payments", "/Merchant/Payments?handler=PayOnline"));

        await ChargeAsync(shop, 230);
        var payments = await owner.PageAsync("/Merchant/Payments");
        Assert.Contains("You owe the courier", payments);
        Assert.Contains("data-confirm-title=\"Pay ৳230 online?\"", payments);

        // Paying sends the merchant to the gateway's page, for the amount worked out on the server
        var (transaction, payment) = await StartAsync(owner);
        Assert.Equal((230m, OnlinePaymentStatus.Started, shop.Id), (payment.Amount, payment.Status, payment.MerchantId));
        Assert.Contains("৳230", await Visit("onedrop").PageAsync($"/Dev/Pay/{transaction}"));

        // A forged return, with an id the gateway never gave, credits nothing
        var forged = await ReturnAsync(transaction, "success", ("val_id", "FAKE-VAL-made-up"), ("status", "VALID"));
        Assert.Equal(HttpStatusCode.Redirect, forged.StatusCode);
        Assert.Null((await PaymentAsync(transaction)).LedgerEntryId);

        // The payer pays on the gateway's page and is posted back; the gateway confirms it and the balance is settled
        var paidPage = await Visit("onedrop").SubmitAsync($"/Dev/Pay/{transaction}", $"/Dev/Pay/{transaction}", ("choice", "bkash"));
        var validationId = ValidationId().Match(paidPage).Groups[1].Value;
        var back = await ReturnAsync(transaction, "success", ("tran_id", transaction), ("val_id", validationId), ("status", "VALID"));
        Assert.Equal($"/Merchant/Payments?payment={payment.Number}", back.Headers.Location!.OriginalString);

        var paid = await PaymentAsync(transaction);
        Assert.Equal((OnlinePaymentStatus.Paid, (decimal?)230m, "BKASH-BKash"), (paid.Status, paid.PaidAmount, paid.Method));
        Assert.Equal(0, await BalanceAsync(shop));

        // The gateway's notice and the browser coming back again credit nothing more
        await ReturnAsync(transaction, "success", ("val_id", validationId));
        await Visit("onedrop").SendAsync(HttpMethod.Post, "/pay/notice", Form(("tran_id", transaction), ("val_id", validationId), ("status", "VALID")));
        Assert.Equal(1, await QueryAsync("onedrop", db => db.LedgerEntries.CountAsync(e => e.MerchantId == shop.Id && e.Amount == 230, Cancel)));

        // The merchant reads it; another merchant does not, and another courier cannot reach it
        var landed = await owner.PageAsync($"/Merchant/Payments?payment={payment.Number}");
        Assert.Contains("Thank you", landed);
        Assert.Contains("Paid by you online", landed);
        Assert.Contains($"Paid online by bKash, {payment.Number}", landed);
        var other = await SignInAsync("onedrop", (await NewMerchantAsync()).Email);
        Assert.DoesNotContain(payment.Number, await other.PageAsync($"/Merchant/Payments?payment={payment.Number}"));
        Assert.Equal(HttpStatusCode.NotFound, (await Visit("rival").SendAsync(HttpMethod.Post, $"/pay/{transaction}/success", Form(("val_id", validationId)))).StatusCode);
        Assert.Contains(payment.Number, await (await SignInAsync("onedrop", "admin@onedrop.test")).PageAsync("/Admin/Payouts"));
    }

    [Fact]
    public async Task A_payer_who_never_comes_back_is_credited_by_the_check_and_an_unfinished_payment_is_closed()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        var owner = await SignInAsync("onedrop", shop.Email);
        var gateway = Factory.Services.GetRequiredService<FakePaymentLog>();

        // Paid on the gateway's page, the browser closed before it came back: the check finds and credits it
        await ChargeAsync(shop, 150);
        var (paidOnly, _) = await StartAsync(owner);
        gateway.Pay(paidOnly, "NAGAD-Nagad");
        await CheckAsync(DateTime.UtcNow);
        Assert.Equal(OnlinePaymentStatus.Paid, (await PaymentAsync(paidOnly)).Status);
        Assert.Equal(0, await BalanceAsync(shop));

        // Started and left: a day on, the check closes it; paid after all, it is still credited
        await ChargeAsync(shop, 80);
        var (left, _) = await StartAsync(owner);
        await CheckAsync(DateTime.UtcNow);
        Assert.NotEqual(OnlinePaymentStatus.Paid, (await PaymentAsync(left)).Status);
        await CheckAsync(DateTime.UtcNow.AddDays(2));
        var closed = await PaymentAsync(left);
        Assert.Equal((OnlinePaymentStatus.Failed, "It was not finished on the payment page within a day."), (closed.Status, closed.Note));

        var late = gateway.Pay(left, "BKASH-BKash")!;
        await ReturnAsync(left, "success", ("val_id", late));
        Assert.Equal(OnlinePaymentStatus.Paid, (await PaymentAsync(left)).Status);
        Assert.Equal(0, await BalanceAsync(shop));
    }

    [Fact]
    public async Task A_risky_payment_waits_for_the_admin_and_a_cancelled_one_credits_nothing()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        var owner = await SignInAsync("onedrop", shop.Email);
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");
        var gateway = Factory.Services.GetRequiredService<FakePaymentLog>();

        // Cancelled on the gateway's page: not paid, nothing credited, and the merchant reads why
        await ChargeAsync(shop, 120);
        var (cancelled, first) = await StartAsync(owner);
        gateway.Fail(cancelled, "It was cancelled on the payment page.");
        await ReturnAsync(cancelled, "cancel", ("tran_id", cancelled), ("status", "CANCELLED"));
        Assert.Equal(OnlinePaymentStatus.Failed, (await PaymentAsync(cancelled)).Status);
        Assert.Contains($"{first.Number} was not paid", await owner.PageAsync($"/Merchant/Payments?payment={first.Number}"));
        Assert.Equal(-120, await BalanceAsync(shop));

        // Paid but marked risky: held, the merchant told, the admin asked
        var (risky, second) = await StartAsync(owner);
        var validationId = gateway.Pay(risky, "VISA-Test Bank", risky: true)!;
        await ReturnAsync(risky, "success", ("val_id", validationId));
        Assert.Equal(OnlinePaymentStatus.Review, (await PaymentAsync(risky)).Status);
        Assert.Equal(-120, await BalanceAsync(shop));
        Assert.Contains("The courier checks it", await owner.PageAsync($"/Merchant/Payments?payment={second.Number}"));
        var payouts = await admin.PageAsync("/Admin/Payouts");
        Assert.Contains("Online payments to check", payouts);
        Assert.Contains(second.Number, payouts);
        Assert.Contains("online payment", await admin.PageAsync("/Admin"));

        // Another courier's admin cannot credit it; this courier's admin does, once
        var rival = await SignInAsync("rival", "admin@rival.test");
        Assert.Contains("not found", await rival.SubmitAsync("/Admin/Payouts", $"/Admin/Payouts?handler=Credit&number={second.Number}"));
        Assert.Contains("was accepted", await admin.SubmitAsync("/Admin/Payouts", $"/Admin/Payouts?handler=Credit&number={second.Number}"));
        Assert.Contains("not waiting for a check", await admin.SubmitAsync("/Admin/Payouts", $"/Admin/Payouts?handler=Credit&number={second.Number}"));
        Assert.Equal(OnlinePaymentStatus.Paid, (await PaymentAsync(risky)).Status);
        Assert.Equal(0, await BalanceAsync(shop));
    }

    /// <summary>Writes a charge on the merchant's balance, so it owes the courier <paramref name="amount"/> more.</summary>
    private Task ChargeAsync(TestMerchant shop, decimal amount)
    {
        return QueryAsync("onedrop", db =>
        {
            db.LedgerEntries.Add(LedgerEntry.Adjust(shop.Id, -amount, "Extra packing", Today).Value);

            return db.SaveChangesAsync(Cancel);
        });
    }

    private Task<decimal> BalanceAsync(TestMerchant shop)
    {
        return QueryAsync("onedrop", db => db.LedgerEntries.Where(e => e.MerchantId == shop.Id && e.PayoutId == null).SumAsync(e => e.Amount, Cancel));
    }

    /// <summary>Asks to pay online, as the merchant's button does, and follows it to the gateway's page.</summary>
    private async Task<(string Transaction, OnlinePayment Payment)> StartAsync(Visitor merchant)
    {
        var started = await merchant.PostFormAsync("/Merchant/Payments", "/Merchant/Payments?handler=PayOnline");
        Assert.Equal(HttpStatusCode.Redirect, started.StatusCode);
        var transaction = PaymentPage().Match(started.Headers.Location!.ToString()).Groups[1].Value;

        return (transaction, await PaymentAsync(transaction));
    }

    /// <summary>The payer's browser posted back from the gateway's site: no sign-in, no anti-forgery token.</summary>
    private Task<HttpResponseMessage> ReturnAsync(string transaction, string outcome, params (string Name, string Value)[] fields)
    {
        return Visit("onedrop").SendAsync(HttpMethod.Post, $"/pay/{transaction}/{outcome}", Form(fields));
    }

    private Task<OnlinePayment> PaymentAsync(string transaction)
    {
        return QueryAsync("onedrop", db => db.OnlinePayments.AsNoTracking().SingleAsync(p => p.TransactionId == transaction, Cancel));
    }

    private async Task CheckAsync(DateTime utcNow)
    {
        await using var scope = await ScopeAsync("onedrop");
        await ActivatorUtilities.CreateInstance<OnlinePaymentsHandler>(scope.ServiceProvider, (TimeProvider)new FakeTimeProvider(utcNow)).CheckAsync(Cancel);
    }

    private static FormUrlEncodedContent Form(params (string Name, string Value)[] fields)
    {
        return new FormUrlEncodedContent(fields.Select(field => KeyValuePair.Create(field.Name, field.Value)));
    }

    [GeneratedRegex("/Dev/Pay/([0-9a-f]+)$")]
    private static partial Regex PaymentPage();

    [GeneratedRegex("name=\"val_id\" value=\"([^\"]+)\"")]
    private static partial Regex ValidationId();
}
