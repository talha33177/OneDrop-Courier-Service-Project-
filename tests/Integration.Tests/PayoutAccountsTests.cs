using System.Net;
using Microsoft.EntityFrameworkCore;
using Domain.Merchants;

namespace Integration.Tests;

/// <summary>A merchant account keeping several payout accounts, and a merchant asking to be paid now.</summary>
public class PayoutAccountsTests(WebAppFactory factory) : AppTests(factory)
{
    [Fact]
    public async Task A_merchant_keeps_several_payout_accounts_and_chooses_the_one_payouts_go_to()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        var owner = await SignInAsync("onedrop", shop.Email);
        var nagad = NewPhone();

        var kept = await owner.SubmitAsync("/Merchant/Settings", "/Merchant/Settings?handler=Payout", ("method", "Nagad"), ("payoutAccount", nagad), ("accountName", "Test Owner"), ("use", "false"));
        Assert.Contains("The account is saved. Choose it for payouts whenever you like.", kept);
        Assert.Equal(PayoutMethod.Bkash, (await MerchantAsync(shop)).PayoutMethod);

        var bank = await owner.SubmitAsync("/Merchant/Settings", "/Merchant/Settings?handler=Payout", ("method", "Bank"), ("payoutAccount", "1501203456789"), ("accountName", "Test Owner, City Bank, Mirpur"), ("use", "true"), ("use", "false"));
        Assert.Contains("Payouts go there from now on", bank);
        Assert.Equal((PayoutMethod.Bank, "1501203456789"), ((await MerchantAsync(shop)).PayoutMethod!.Value, (await MerchantAsync(shop)).PayoutAccount!));
        Assert.Contains("That account is saved already", await owner.SubmitAsync("/Merchant/Settings", "/Merchant/Settings?handler=Payout", ("method", "Nagad"), ("payoutAccount", nagad), ("accountName", "Test Owner"), ("use", "false")));

        var saved = await QueryAsync("onedrop", db => db.MerchantPayoutAccounts.Where(a => a.AccountId == shop.Id).OrderBy(a => a.Id).ToListAsync(Cancel));
        Assert.Equal([PayoutMethod.Nagad, PayoutMethod.Bank], saved.Select(a => a.Method));
        Assert.Contains("Payouts go to this account. Choose another one for payouts first.", await owner.SubmitAsync("/Merchant/Settings", $"/Merchant/Settings?handler=RemovePayout&id={saved[1].Id}"));
        Assert.Contains("Payouts go to that account from now on", await owner.SubmitAsync("/Merchant/Settings", $"/Merchant/Settings?handler=UsePayout&id={saved[0].Id}"));
        Assert.Equal(PayoutMethod.Nagad, (await MerchantAsync(shop)).PayoutMethod);
        Assert.Contains("The account is removed", await owner.SubmitAsync("/Merchant/Settings", $"/Merchant/Settings?handler=RemovePayout&id={saved[1].Id}"));
        Assert.DoesNotContain("1501203456789", await owner.PageAsync("/Merchant/Settings"));

        // Another account's saved account is not found, and stays as it was
        var other = await SignInAsync("onedrop", (await NewMerchantAsync()).Email);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostFormAsync("/Merchant/Settings", $"/Merchant/Settings?handler=UsePayout&id={saved[0].Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostFormAsync("/Merchant/Settings", $"/Merchant/Settings?handler=RemovePayout&id={saved[0].Id}")).StatusCode);
        Assert.False((await QueryAsync("onedrop", db => db.MerchantPayoutAccounts.SingleAsync(a => a.Id == saved[0].Id, Cancel))).Archived);
    }

    [Fact]
    public async Task An_account_the_admin_sets_is_kept_among_the_merchants_accounts()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");

        await admin.SubmitAsync($"/Admin/Merchant/{shop.Id}", $"/Admin/Merchant/{shop.Id}?handler=Payout", ("method", "Bank"), ("payoutAccount", "2201203456789"), ("accountName", "Owner, Dutch-Bangla Bank, Gulshan"));

        Assert.Equal(PayoutMethod.Bank, (await MerchantAsync(shop)).PayoutMethod);
        Assert.Contains("2201203456789", await (await SignInAsync("onedrop", shop.Email)).PageAsync("/Merchant/Settings"));
        Assert.Equal(1, await QueryAsync("onedrop", db => db.MerchantPayoutAccounts.CountAsync(a => a.AccountId == shop.Id && a.Number == "2201203456789", Cancel)));
    }

    private Task<Merchant> MerchantAsync(TestMerchant shop)
    {
        return QueryAsync("onedrop", db => db.Merchants.AsNoTracking().SingleAsync(m => m.Id == shop.Id, Cancel));
    }
}
