using Domain.Payments;

namespace Domain.Tests;

/// <summary>A merchant paying what it owes online: the amount it may pay, and money the gateway confirms credited exactly once.</summary>
public class OnlinePaymentTests
{
    private static readonly PaymentLimits Limits = new(10, 500_000);

    private static readonly DateOnly Today = new(2026, 10, 8);

    private static readonly DateTime Now = new(2026, 10, 8, 6, 0, 0, DateTimeKind.Utc);

    private static OnlinePayment Started(decimal owed = 230)
    {
        var payment = Build.WithId(OnlinePayment.Start(7, owed, "BDT", Limits).Value, 11);
        typeof(OnlinePayment).GetProperty(nameof(OnlinePayment.Number))!.SetValue(payment, "PAY-100001");

        return payment;
    }

    private static GatewayReceipt Receipt(OnlinePayment payment, decimal? amount = null, string currency = "BDT", bool risky = false)
    {
        return new GatewayReceipt(payment.TransactionId, "VAL-1", amount ?? payment.Amount, currency, (amount ?? payment.Amount) - 5, "BKASH-BKash", "BANK-1", risky, risky ? "Card used from abroad" : null);
    }

    [Fact]
    public void A_merchant_pays_only_what_it_owes_within_the_gateways_limits()
    {
        var payment = OnlinePayment.Start(7, 230, "BDT", Limits).Value;

        Assert.Equal((7L, 230m, "BDT", OnlinePaymentStatus.Started), (payment.MerchantId, payment.Amount, payment.Currency, payment.Status));
        Assert.Equal(OnlinePayment.TransactionIdLength, payment.TransactionId.Length);
        Assert.NotEqual(payment.TransactionId, OnlinePayment.Start(7, 230, "BDT", Limits).Value.TransactionId);
        Assert.Equal("payment.nothingOwed", OnlinePayment.Start(7, 0, "BDT", Limits).Error?.Code);
        Assert.Equal("payment.nothingOwed", OnlinePayment.Start(7, -50, "BDT", Limits).Error?.Code);
        Assert.Equal("payment.belowMinimum", OnlinePayment.Start(7, 9.5m, "BDT", Limits).Error?.Code);
        Assert.Equal("payment.aboveMaximum", OnlinePayment.Start(7, 500_001, "BDT", Limits).Error?.Code);
    }

    [Fact]
    public void Confirmed_money_is_credited_once_however_often_the_confirmation_arrives()
    {
        var payment = Started();

        payment.Confirm(Receipt(payment), Today, Now);
        var credit = payment.LedgerEntry!;
        payment.Confirm(Receipt(payment), Today, Now.AddMinutes(1));

        Assert.Equal(OnlinePaymentStatus.Paid, payment.Status);
        Assert.Same(credit, payment.LedgerEntry);
        Assert.Equal((7L, LedgerEntryKind.Adjustment, 230m, Today, (long?)null), (credit.MerchantId, credit.Kind, credit.Amount, credit.EntryDate, credit.ParcelId));
        Assert.Equal("Paid online by bKash, PAY-100001", credit.Note);
        Assert.Equal((230m, 225m, "VAL-1", "BANK-1", (DateTime?)Now), (payment.PaidAmount, payment.StoreAmount, payment.ValidationId, payment.BankTransactionId, payment.ConfirmedOn));

        // A failure reported afterwards changes nothing
        payment.Fail("The payment page said it failed.");
        Assert.Equal(OnlinePaymentStatus.Paid, payment.Status);
    }

    [Fact]
    public void A_payment_that_failed_on_its_page_is_still_credited_when_the_gateway_confirms_it()
    {
        var payment = Started();

        payment.Fail(null);
        Assert.Equal((OnlinePaymentStatus.Failed, "It was not finished on the payment page."), (payment.Status, payment.Note));
        Assert.Null(payment.LedgerEntry);

        payment.Confirm(Receipt(payment), Today, Now);
        Assert.Equal(OnlinePaymentStatus.Paid, payment.Status);
        Assert.Equal(230, payment.LedgerEntry!.Amount);
    }

    [Fact]
    public void A_risky_payment_or_another_amount_waits_for_the_courier_to_credit_or_refund()
    {
        var risky = Started();
        risky.Confirm(Receipt(risky, risky: true), Today, Now);
        Assert.Equal(OnlinePaymentStatus.Review, risky.Status);
        Assert.Null(risky.LedgerEntry);
        Assert.Equal("The gateway marked the payment risky: Card used from abroad.", risky.Note);

        // Credited by the courier, for what the gateway took, once
        Assert.True(risky.Accept(Today).IsSuccess);
        Assert.Equal((OnlinePaymentStatus.Paid, 230m), (risky.Status, risky.LedgerEntry!.Amount));
        Assert.Equal("payment.notInReview", risky.Accept(Today).Error?.Code);

        var less = Started();
        less.Confirm(Receipt(less, amount: 200), Today, Now);
        Assert.Equal((OnlinePaymentStatus.Review, 200m), (less.Status, less.PaidAmount));
        Assert.Equal("The gateway confirmed 200.00 BDT, not the 230.00 BDT asked for.", less.Note);
        Assert.True(less.Accept(Today).IsSuccess);
        Assert.Equal(200, less.LedgerEntry!.Amount);

        // In another currency it cannot be credited, only refunded, with what was done
        var dollars = Started();
        dollars.Confirm(Receipt(dollars, amount: 2, currency: "USD"), Today, Now);
        Assert.Equal(OnlinePaymentStatus.Review, dollars.Status);
        Assert.Equal("payment.currency", dollars.Accept(Today).Error?.Code);
        Assert.Equal("payment.note", dollars.Refund(" ", Now).Error?.Code);
        Assert.True(dollars.Refund("Refunded in full through SSLCommerz", Now).IsSuccess);
        Assert.Equal((OnlinePaymentStatus.Refunded, "Refunded in full through SSLCommerz"), (dollars.Status, dollars.Note));
        Assert.Null(dollars.LedgerEntry);

        // A confirmation arriving again later does not credit a refunded payment
        dollars.Confirm(Receipt(dollars), Today, Now);
        Assert.Equal(OnlinePaymentStatus.Refunded, dollars.Status);
    }

    [Theory]
    [InlineData("BKASH-BKash", "bKash")]
    [InlineData("NAGAD-Nagad", "Nagad")]
    [InlineData("DBBLMOBILEB-Dbbl Mobile Banking", "Rocket")]
    [InlineData("VISA-Dutch Bangla", "Visa card, Dutch Bangla")]
    [InlineData("MASTER-City Bank", "Mastercard, City Bank")]
    [InlineData("IBBL-Islami Bank", "Islami Bank")]
    [InlineData("Something new", "Something new")]
    [InlineData(" ", null)]
    public void The_gateways_label_for_how_it_was_paid_is_read_as_people_say_it(string label, string? name)
    {
        Assert.Equal(name, OnlinePayment.MethodName(label));
    }

    [Fact]
    public void Only_the_gateways_answer_for_its_own_transaction_confirms_a_saved_payment()
    {
        var payment = Started();
        var other = Started();

        Assert.Throws<InvalidOperationException>(() => payment.Confirm(Receipt(other), Today, Now));
        Assert.Throws<InvalidOperationException>(() =>
        {
            var unsaved = OnlinePayment.Start(7, 230, "BDT", Limits).Value;
            unsaved.Confirm(Receipt(unsaved), Today, Now);
        });
        Assert.Equal("payment.notInReview", payment.Refund("Refunded", Now).Error?.Code);
    }
}
