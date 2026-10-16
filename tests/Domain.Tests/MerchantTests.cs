using Domain.Common;
using Domain.Merchants;

namespace Domain.Tests;

/// <summary>A merchant's account: signing up, approval, suspension, its payout account and pickup points.</summary>
public class MerchantTests
{
    private static readonly MerchantProfile Profile = new("Gadget BD", "Tanvir Ahmed", "01711000002", "gadget@example.com", "Shop 12, Dhanmondi");

    [Fact]
    public void A_signed_up_merchant_waits_for_approval_and_one_the_admin_adds_books_at_once()
    {
        var signedUp = Merchant.SignUp(Profile).Value;
        var added = Merchant.Add(Profile).Value;

        Assert.Equal(MerchantStatus.Pending, signedUp.Status);
        Assert.False(signedUp.CanBook);
        Assert.True(added.CanBook);
        Assert.Equal("+8801711000002", added.ContactPhone);
    }

    [Theory]
    [InlineData("", "Owner", "01711000002", null, "Address", "merchant.name")]
    [InlineData("Shop", "", "01711000002", null, "Address", "merchant.owner")]
    [InlineData("Shop", "Owner", "123", null, "Address", "phone.invalid")]
    [InlineData("Shop", "Owner", "01711000002", "not-an-email", "Address", "merchant.email")]
    [InlineData("Shop", "Owner", "01711000002", null, " ", "merchant.address")]
    public void A_profile_with_a_missing_or_bad_detail_is_refused(string name, string owner, string phone, string? email, string address, string code)
    {
        Assert.Equal(code, Merchant.SignUp(new MerchantProfile(name, owner, phone, email, address)).Error?.Code);
    }

    [Fact]
    public void Approval_suspension_and_reactivation_move_only_from_the_right_status()
    {
        var merchant = Merchant.SignUp(Profile).Value;

        Assert.Equal("merchant.reactivate", merchant.Reactivate().Error?.Code);
        Assert.True(merchant.Approve().IsSuccess);
        Assert.Equal("merchant.approve", merchant.Approve().Error?.Code);
        Assert.True(merchant.Suspend().IsSuccess);
        Assert.False(merchant.CanBook);
        Assert.Equal("merchant.suspend", merchant.Suspend().Error?.Code);
        Assert.True(merchant.Reactivate().IsSuccess);
        Assert.True(merchant.CanBook);
    }

    [Fact]
    public void A_payout_account_is_a_wallet_number_or_a_bank_account_with_its_name()
    {
        var merchant = Merchant.Add(Profile).Value;

        Assert.False(merchant.HasPayoutAccount);
        Assert.Equal("merchant.payout.account", merchant.SetPayoutAccount(PayoutMethod.Bkash, "1234", "Tanvir").Error?.Code);
        Assert.Equal("merchant.payout.name", merchant.SetPayoutAccount(PayoutMethod.Nagad, "01711000002", " ").Error?.Code);
        Assert.Equal("merchant.payout.account", merchant.SetPayoutAccount(PayoutMethod.Bank, "12ab", "Tanvir, City Bank").Error?.Code);
        Assert.False(merchant.HasPayoutAccount);

        Assert.True(merchant.SetPayoutAccount(PayoutMethod.Bkash, "017-1100-0002", "Tanvir Ahmed").IsSuccess);
        Assert.Equal("+8801711000002", merchant.PayoutAccount);
        Assert.True(merchant.SetPayoutAccount(PayoutMethod.Bank, "1501203456789", "Tanvir Ahmed, City Bank, Dhanmondi").IsSuccess);
        Assert.Equal("1501203456789", merchant.PayoutAccount);
        Assert.True(merchant.HasPayoutAccount);
    }

    [Fact]
    public void A_pickup_point_needs_a_name_an_address_and_a_phone()
    {
        Assert.Equal("pickupPoint.name", PickupPoint.Create(7, 1, " ", "House 1", "01711000002", true).Error?.Code);
        Assert.Equal("pickupPoint.address", PickupPoint.Create(7, 1, "Shop", "", "01711000002", true).Error?.Code);
        Assert.Equal("phone.invalid", PickupPoint.Create(7, 1, "Shop", "House 1", "9", true).Error?.Code);

        var point = PickupPoint.Create(7, 1, " Shop ", " House 1 ", "01711000002", true).Value;
        Assert.Equal("Shop", point.Name);
        Assert.True(point.IsDefault);
        point.MakeDefault(false);
        Assert.False(point.IsDefault);
    }

    [Fact]
    public void A_pickup_point_keeps_the_door_pinned_on_the_map_and_can_have_it_taken_off()
    {
        var pinned = PickupPoint.Create(7, 1, "Shop", "House 1", "01711000002", true, GeoPoint.Find("23.806964, 90.368393")).Value;

        Assert.Equal("23.806964,90.368393", pinned.Pin!.Text);

        // Not pinned: the rider is guided by the address instead, and the pin can be removed again
        var byAddress = PickupPoint.Create(7, 1, "Shop", "House 1", "01711000002", true).Value;
        Assert.Null(byAddress.Pin);
        Assert.True(pinned.Change(1, "Shop", "House 1", "01711000002").IsSuccess);
        Assert.Null(pinned.Pin);
    }

    [Fact]
    public void A_business_added_to_an_account_takes_the_account_s_owner_approval_and_payout_account()
    {
        var main = Merchant.Add(Profile).Value;
        main.SetPayoutAccount(PayoutMethod.Bkash, "01711000003", "Tanvir Ahmed");

        var business = Merchant.AddBusiness(main, new MerchantProfile("Gadget BD Kids", "Someone else", "01711000004", null, "Shop 14, Dhanmondi"), 0, 10).Value;

        Assert.False(business.IsMainProfile);
        Assert.Equal(main.Id, business.AccountId);
        Assert.Equal("Tanvir Ahmed", business.OwnerName);
        Assert.True(business.CanBook);
        Assert.Equal(PayoutMethod.Bkash, business.PayoutMethod);
        Assert.Equal("+8801711000003", business.PayoutAccount);
        Assert.Equal("+8801711000004", business.ContactPhone);
    }

    [Fact]
    public void A_business_of_an_account_waiting_for_approval_waits_too_and_follows_the_approval()
    {
        var main = Merchant.SignUp(Profile).Value;
        var business = Merchant.AddBusiness(main, Profile with { Name = "Gadget BD Kids" }, 0, 10).Value;
        Assert.False(business.CanBook);

        main.Approve();
        business.FollowAccount(main);

        Assert.True(business.CanBook);
    }

    [Fact]
    public void No_business_is_added_to_a_suspended_account_or_under_another_business()
    {
        var main = Merchant.Add(Profile).Value;
        var business = Merchant.AddBusiness(main, Profile with { Name = "Gadget BD Kids" }, 0, 10).Value;

        Assert.Equal("merchant.business.main", Merchant.AddBusiness(business, Profile with { Name = "Third" }, 1, 10).Error?.Code);
        main.Suspend();
        Assert.Equal("merchant.business.suspended", Merchant.AddBusiness(main, Profile with { Name = "Third" }, 1, 10).Error?.Code);
    }

    [Fact]
    public void An_account_adds_businesses_up_to_the_courier_s_most_besides_its_main_profile()
    {
        var main = Merchant.Add(Profile).Value;
        var kids = Profile with { Name = "Gadget BD Kids" };

        Assert.True(Merchant.AddBusiness(main, kids, 9, 10).IsSuccess);
        Assert.Equal("merchant.business.limit", Merchant.AddBusiness(main, kids, 10, 10).Error?.Code);
        Assert.True(Merchant.AddBusiness(main, kids, 50, null).IsSuccess);
    }
}
