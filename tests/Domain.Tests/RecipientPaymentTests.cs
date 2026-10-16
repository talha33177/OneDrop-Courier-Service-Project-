using Domain.Parcels;
using Domain.Payments;

namespace Domain.Tests;

/// <summary>
/// A recipient paying a parcel's cash on delivery online: the rider then collects nothing, the merchant is still paid the
/// whole amount, and money a parcel cannot take, or paid for a parcel that never arrives, is owed back.
/// </summary>
public class RecipientPaymentTests
{
    private static readonly PaymentLimits Limits = new(10, 500_000);

    private static readonly DateOnly Today = new(2026, 10, 12);

    private static readonly DateTime Now = new(2026, 10, 12, 6, 0, 0, DateTimeKind.Utc);

    private static OnlinePayment Started(Parcel parcel)
    {
        var payment = Build.WithId(OnlinePayment.StartForParcel(parcel, "BDT", Limits).Value, 21);
        typeof(OnlinePayment).GetProperty(nameof(OnlinePayment.Number))!.SetValue(payment, "PAY-100002");

        return payment;
    }

    private static GatewayReceipt Receipt(OnlinePayment payment, bool risky = false)
    {
        return new GatewayReceipt(payment.TransactionId, "VAL-2", payment.Amount, "BDT", payment.Amount - 30, "BKASH-BKash", "BANK-2", risky);
    }

    [Fact]
    public void A_recipient_pays_the_parcels_own_cash_on_delivery_while_it_is_on_its_way()
    {
        var parcel = Build.Parcel(cod: 1250);
        var payment = OnlinePayment.StartForParcel(parcel, "BDT", Limits).Value;

        Assert.Equal((7L, (long?)parcel.Id, 1250m, true), (payment.MerchantId, payment.ParcelId, payment.Amount, payment.IsForParcel));
        Assert.Equal("parcel.pay.nothing", OnlinePayment.StartForParcel(Build.Parcel(cod: 0), "BDT", Limits).Error?.Code);
        Assert.Equal("payment.limits", OnlinePayment.StartForParcel(Build.Parcel(cod: 5), "BDT", Limits).Error?.Code);
        Assert.Equal("parcel.pay.status", OnlinePayment.StartForParcel(Build.OutForDelivery().Let(p => p.Deliver(1250, null, Now)), "BDT", Limits).Error?.Code);
        Assert.Equal("parcel.pay.status", OnlinePayment.StartForParcel(Build.Returning(), "BDT", Limits).Error?.Code);
        Assert.True(OnlinePayment.StartForParcel(Build.OutForDelivery(), "BDT", Limits).IsSuccess);
    }

    [Fact]
    public void A_confirmed_payment_writes_no_line_of_its_own_and_the_parcel_takes_it_once()
    {
        var parcel = Build.Parcel(cod: 1250);
        var payment = Started(parcel);

        payment.Confirm(Receipt(payment), Today, Now);

        Assert.Equal(OnlinePaymentStatus.Paid, payment.Status);
        Assert.Null(payment.LedgerEntry);
        Assert.True(parcel.TakeOnlinePayment(payment.PaidAmount!.Value, payment.Number).IsSuccess);
        Assert.Equal((1250m, 0m), (parcel.PaidOnline, parcel.AmountToCollect));
        Assert.Contains(parcel.Events, e => e.Note.Contains("paid online by the recipient (PAY-100002)"));
        Assert.Equal("parcel.pay.paid", parcel.TakeOnlinePayment(1250, "PAY-100003").Error?.Code);
    }

    [Fact]
    public void A_paid_parcel_is_delivered_whole_with_nothing_collected_and_the_merchant_paid_in_full()
    {
        var parcel = Build.OutForDelivery(cod: 1250);
        parcel.TakeOnlinePayment(1250, "PAY-100002");

        Assert.Equal("parcel.deliver.amount", parcel.Deliver(1250, null, Now).Error?.Code);
        Assert.True(parcel.Deliver(0, null, Now).IsSuccess);

        Assert.Equal(ParcelStatus.Delivered, parcel.Status);
        Assert.Equal((1250m, 13m), (parcel.CollectedAmount, parcel.CodCharge));
        var lines = LedgerEntry.For(parcel, Today);
        Assert.Equal(1250m, lines.Single(l => l.Kind == LedgerEntryKind.Cod).Amount);
    }

    [Fact]
    public void Once_paid_the_cash_on_delivery_no_longer_changes()
    {
        var booked = Build.Parcel(cod: 1250);
        booked.TakeOnlinePayment(1250, "PAY-100002");
        var moving = Build.OutForDelivery(cod: 1250);
        moving.TakeOnlinePayment(1250, "PAY-100003");

        Assert.Equal("parcel.cod.paid", moving.ChangeCod(900, "Discount").Error?.Code);
        Assert.Equal("request.cod.paid", ParcelRequest.Ask(moving, ParcelRequestKind.ChangeCod, 900, "Discount", 42).Error?.Code);
        Assert.True(ParcelRequest.Ask(moving, ParcelRequestKind.Cancel, null, "Customer changed their mind", 42).IsSuccess);
        Assert.Equal(1250m, moving.CodAmount);
        Assert.Equal("parcel.pay.amount", Build.Parcel(cod: 1250).TakeOnlinePayment(1000, "PAY-100004").Error?.Code);
    }

    [Fact]
    public void Money_for_a_parcel_that_will_not_arrive_is_owed_back_and_refunded_once()
    {
        var parcel = Build.Parcel(cod: 1250);
        var payment = Started(parcel);
        payment.Confirm(Receipt(payment), Today, Now);
        parcel.TakeOnlinePayment(1250, payment.Number);
        parcel.Cancel("Customer changed their mind", Now);

        Assert.True(parcel.OwesRefund);
        payment.OweBack("Cancelled");
        Assert.Equal(OnlinePaymentStatus.RefundDue, payment.Status);

        payment.RefundAnswered(RefundState.Refused, null, "Bank is down", Now);
        Assert.True(payment.RefundRefused);
        Assert.Equal(OnlinePaymentStatus.RefundDue, payment.Status);

        payment.RefundAnswered(RefundState.Processing, "REF-1", null, Now.AddHours(1));
        Assert.False(payment.RefundRefused);
        payment.RefundAnswered(RefundState.Done, null, null, Now.AddHours(2));

        Assert.Equal((OnlinePaymentStatus.Refunded, "REF-1", (DateTime?)Now.AddHours(2)), (payment.Status, payment.RefundReference, payment.RefundedOn));
        payment.RefundAnswered(RefundState.Refused, null, "late answer", Now.AddHours(3));
        Assert.Equal(OnlinePaymentStatus.Refunded, payment.Status);
    }

    [Fact]
    public void A_risky_payment_is_held_and_only_a_recipients_payment_is_ever_owed_back()
    {
        var payment = Started(Build.Parcel(cod: 1250));
        payment.Confirm(Receipt(payment, risky: true), Today, Now);

        Assert.Equal(OnlinePaymentStatus.Review, payment.Status);
        payment.OweBack("Not safe");
        Assert.Equal(OnlinePaymentStatus.RefundDue, payment.Status);
        Assert.True(payment.Refund("Refunded from the gateway's panel", Now).IsSuccess);
        Assert.Equal(OnlinePaymentStatus.Refunded, payment.Status);

        var merchants = OnlinePayment.Start(7, 230, "BDT", Limits).Value;
        Assert.Throws<InvalidOperationException>(() => merchants.OweBack("No"));
    }
}
