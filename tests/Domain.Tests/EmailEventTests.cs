using Domain.Common;
using Domain.Merchants;
using Domain.Payments;

namespace Domain.Tests;

/// <summary>
/// The changes people are emailed about raise an event, once, and only the change itself: a refused change, or the
/// businesses that follow their account, raise none.
/// </summary>
public class EmailEventTests
{
    private static MerchantNotice[] Notices(Entity entity)
    {
        return [.. entity.GetDomainEvents().OfType<MerchantAccountChanged>().Select(e => e.Notice)];
    }

    [Fact]
    public void A_sign_up_and_each_change_to_the_account_raise_their_notice_and_a_refused_change_none()
    {
        var merchant = Merchant.SignUp(new MerchantProfile("Dhaka Fashion", "Talha", "01711000009", "shop@example.com", "House 1, Mirpur")).Value;
        Assert.Equal([MerchantNotice.SignedUp], Notices(merchant));

        merchant.ClearDomainEvents();
        Assert.True(merchant.Approve().IsSuccess);
        Assert.True(merchant.Approve().IsFailure);
        Assert.True(merchant.HoldPayouts("Checking a damage claim").IsSuccess);
        Assert.True(merchant.ReleasePayouts().IsSuccess);
        Assert.True(merchant.Suspend().IsSuccess);
        Assert.True(merchant.Reactivate().IsSuccess);
        Assert.Equal(
            [MerchantNotice.Approved, MerchantNotice.PayoutsHeld, MerchantNotice.PayoutsReleased, MerchantNotice.Suspended, MerchantNotice.Reactivated],
            Notices(merchant));

        // A shop the courier adds itself is active at once and gets no sign-up notice
        Assert.Empty(Notices(Build.Merchant()));
    }

    [Fact]
    public void Only_an_adjustment_the_courier_writes_by_hand_is_told_and_an_online_payment_s_credit_is_not()
    {
        var byHand = LedgerEntry.Adjust(7, 200, "Compensation for a damaged parcel", Build.Today).Value;
        Assert.Empty(byHand.GetDomainEvents());
        byHand.TellMerchant();
        Assert.Single(byHand.GetDomainEvents().OfType<AdjustmentWritten>());

        var line = LedgerEntry.For(Build.OutForDelivery().Let(p => p.Deliver(1250, null, DateTime.UtcNow)), Build.Today)[0];
        Assert.Throws<InvalidOperationException>(line.TellMerchant);
    }

    [Fact]
    public void A_refused_payout_request_is_told_once_and_a_withdrawn_one_is_not()
    {
        var merchant = Build.Merchant();
        Assert.True(merchant.SetPayoutAccount(PayoutMethod.Bkash, "01711000009", "Nusrat Jahan").IsSuccess);

        var refused = PayoutRequest.Ask(merchant, 930).Value;
        Assert.True(refused.Refuse("Cash still with riders", DateTime.UtcNow).IsSuccess);
        Assert.True(refused.Refuse("Again", DateTime.UtcNow).IsFailure);
        Assert.Single(refused.GetDomainEvents().OfType<PayoutRequestRefused>());

        var withdrawn = PayoutRequest.Ask(merchant, 930).Value;
        Assert.True(withdrawn.Withdraw(DateTime.UtcNow).IsSuccess);
        Assert.Empty(withdrawn.GetDomainEvents());
    }
}
