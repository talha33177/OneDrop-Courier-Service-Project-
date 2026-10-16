using Domain.Merchants;
using Domain.Payments;

namespace Domain.Tests;

/// <summary>A merchant asking to be paid now: only with money, an account and no hold; answered once.</summary>
public class PayoutRequestTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 6, 0, 0, DateTimeKind.Utc);

    private static Merchant Payable()
    {
        var merchant = Build.Merchant();
        Assert.True(merchant.SetPayoutAccount(PayoutMethod.Bkash, "01711000009", "Nusrat Jahan").IsSuccess);

        return merchant;
    }

    [Fact]
    public void A_merchant_asks_only_with_money_to_pay_an_account_to_pay_it_to_and_no_hold()
    {
        var merchant = Payable();

        var asked = PayoutRequest.Ask(merchant, 930).Value;
        Assert.Equal((merchant.Id, 930m, PayoutRequestStatus.Open), (asked.MerchantId, asked.AmountAsked, asked.Status));
        Assert.Equal("payoutRequest.nothing", PayoutRequest.Ask(merchant, 0).Error?.Code);
        Assert.Equal("payoutRequest.nothing", PayoutRequest.Ask(merchant, -40).Error?.Code);
        Assert.Equal("payoutRequest.account", PayoutRequest.Ask(Build.Merchant(), 930).Error?.Code);

        Assert.True(merchant.HoldPayouts("Checking a damage claim").IsSuccess);
        Assert.Equal("payoutRequest.held", PayoutRequest.Ask(merchant, 930).Error?.Code);
    }

    [Fact]
    public void A_request_is_answered_once_paid_refused_with_a_reason_or_withdrawn()
    {
        var merchant = Payable();

        var refused = PayoutRequest.Ask(merchant, 930).Value;
        Assert.Equal("payoutRequest.answer", refused.Refuse("  ", Now).Error?.Code);
        Assert.True(refused.Refuse("The rider has not handed the cash in yet", Now).IsSuccess);
        Assert.Equal((PayoutRequestStatus.Refused, "The rider has not handed the cash in yet", (DateTime?)Now), (refused.Status, refused.Answer, refused.AnsweredOn));
        Assert.Equal("payoutRequest.answered", refused.Withdraw(Now).Error?.Code);

        var withdrawn = PayoutRequest.Ask(merchant, 930).Value;
        Assert.True(withdrawn.Withdraw(Now).IsSuccess);
        Assert.Equal(PayoutRequestStatus.Withdrawn, withdrawn.Status);
        Assert.Equal("payoutRequest.answered", withdrawn.Refuse("Too late", Now).Error?.Code);
    }

    [Fact]
    public void A_request_is_paid_by_a_saved_payout_of_its_own_merchant()
    {
        var merchant = Payable();
        var request = PayoutRequest.Ask(merchant, 930).Value;
        var line = LedgerEntry.Adjust(merchant.Id, 930, "Cash collected", new DateOnly(2026, 10, 8)).Value;
        var payout = Build.WithId(Payout.Of(merchant, new DateOnly(2026, 10, 8), [line])!, 77);

        Assert.Throws<InvalidOperationException>(() => request.Pay(Payout.Of(merchant, new DateOnly(2026, 10, 8), [LedgerEntry.Adjust(merchant.Id, 10, "x", new DateOnly(2026, 10, 8)).Value])!, Now));
        Assert.True(request.Pay(payout, Now).IsSuccess);
        Assert.Equal((PayoutRequestStatus.Paid, (long?)77), (request.Status, request.PayoutId));
        Assert.Equal("payoutRequest.answered", request.Pay(payout, Now).Error?.Code);
    }
}
