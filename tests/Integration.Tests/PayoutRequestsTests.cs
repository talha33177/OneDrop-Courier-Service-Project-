using Microsoft.EntityFrameworkCore;
using Domain.Payments;

namespace Integration.Tests;

/// <summary>
/// A merchant asks to be paid now instead of the next morning; the courier's admin decides, seeing the cash still with
/// riders and what the parcels on their way would cost if they came back, and pays at once or refuses with a reason.
/// These run at the rival courier: PayoutTests runs OneDrop's daily payouts as of tomorrow, which would pay these
/// merchants' lines out from under them.
/// </summary>
public class PayoutRequestsTests(WebAppFactory factory) : AppTests(factory)
{
    [Fact]
    public async Task The_admin_sees_what_is_at_stake_and_pays_a_request_once()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync("rival", "Rival Town");
        var rider = await NewRiderAsync("RVL", "rival");
        var delivered = await BookAsync(shop.ApiKey, area: "Rival Town", cod: 1000);
        var onItsWay = await BookAsync(shop.ApiKey, area: "Rival Town", cod: 500);
        var hub = await SignInAsync("rival", "hub@rival.test");
        foreach (var code in new[] { delivered, onItsWay })
        {
            await hub.SubmitAsync("/Hub/Scan?hub=RVL", "/Hub/Scan?mode=Receive&hub=RVL", ("Code", code));
        }

        // Delivered by a rider who has not handed the cash in yet: ৳1,000 less the rival's ৳70 is the balance
        await hub.SubmitAsync("/Hub/Assign?hub=RVL", "/Hub/Assign?hub=RVL", ("riderId", $"{rider.Id}"), ("codes", delivered));
        await (await SignInAsync("rival", rider.Email)).SubmitAsync($"/Rider/Delivery/{delivered}", $"/Rider/Delivery/{delivered}?handler=Deliver", ("collected", "1000"), ("reason", ""));

        // The merchant asks, once
        var owner = await SignInAsync("rival", shop.Email);
        Assert.Contains("Ask to be paid now", await owner.PageAsync("/Merchant/Payments"));
        Assert.Contains("Your request is with the courier", await owner.SubmitAsync("/Merchant/Payments", "/Merchant/Payments?handler=Ask"));
        Assert.Contains("You asked to be paid now", await owner.PageAsync("/Merchant/Payments"));
        Assert.Contains("You have asked already", await owner.SubmitAsync("/Merchant/Payments", "/Merchant/Payments?handler=Ask"));
        var request = await QueryAsync("rival", db => db.PayoutRequests.AsNoTracking().SingleAsync(r => r.MerchantId == shop.Id, Cancel));
        Assert.Equal((PayoutRequestStatus.Open, 930m), (request.Status, request.AmountAsked));

        // The admin sees the cash still with the rider and the parcel on its way (৳70 and ৳20 if it came back), in the
        // list, the bell and the menu
        var admin = await SignInAsync("rival", "admin@rival.test");
        var payouts = await admin.PageAsync("/Admin/Payouts");
        Assert.Contains("Asking to be paid now", payouts);
        Assert.Contains(shop.Name, payouts);
        Assert.Contains("runs not closed yet", payouts);
        Assert.Contains("1 parcel, up to", payouts);
        Assert.Contains("৳90", payouts);
        var dashboard = await admin.PageAsync("/Admin");
        Assert.Contains("asking to be paid now", dashboard);
        Assert.Contains("Merchant payouts<span class=\"nav-work\"", dashboard);

        // Another courier's admin cannot pay it; this courier's admin does, once
        var other = await SignInAsync("onedrop", "admin@onedrop.test");
        Assert.Contains("not found", await other.SubmitAsync("/Admin/Payouts", $"/Admin/Payouts?handler=PayRequest&id={request.Id}"));
        Assert.Contains("was sent to the merchant", await admin.SubmitAsync("/Admin/Payouts", $"/Admin/Payouts?handler=PayRequest&id={request.Id}"));
        Assert.Contains("answered already", await admin.SubmitAsync("/Admin/Payouts", $"/Admin/Payouts?handler=PayRequest&id={request.Id}"));

        var payout = await QueryAsync("rival", db => db.Payouts.AsNoTracking().SingleAsync(p => p.MerchantId == shop.Id, Cancel));
        var paid = await QueryAsync("rival", db => db.PayoutRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id, Cancel));
        Assert.Equal((PayoutStatus.Paid, 930m), (payout.Status, payout.Amount));
        Assert.Equal((PayoutRequestStatus.Paid, (long?)payout.Id), (paid.Status, paid.PayoutId));

        // Nothing is left to ask for
        var after = await owner.PageAsync("/Merchant/Payments");
        Assert.DoesNotContain("Ask to be paid now", after);
        Assert.Contains(payout.Number, after);
    }

    [Fact]
    public async Task A_refused_request_says_why_and_an_open_one_can_be_taken_back()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync("rival", "Rival Town");
        var code = await BookAsync(shop.ApiKey, area: "Rival Town", cod: 800);
        var rider = await NewRiderAsync("RVL", "rival");
        await QueryAsync("rival", async db =>
        {
            var parcel = await db.Parcels.SingleAsync(p => p.TrackingCode == code, Cancel);
            parcel.ReceiveAt(parcel.DeliveryHubId, Today);
            parcel.AssignTo(rider.Id, parcel.DeliveryHubId);
            parcel.Deliver(800, null, DateTime.UtcNow);
            db.LedgerEntries.AddRange(LedgerEntry.For(parcel, Today));

            return await db.SaveChangesAsync(Cancel);
        });
        var owner = await SignInAsync("rival", shop.Email);
        var admin = await SignInAsync("rival", "admin@rival.test");

        // Refused, with why: the merchant reads it on Payments and in the bell
        await owner.SubmitAsync("/Merchant/Payments", "/Merchant/Payments?handler=Ask");
        var first = await QueryAsync("rival", db => db.PayoutRequests.Where(r => r.MerchantId == shop.Id).Select(r => r.Id).SingleAsync(Cancel));
        Assert.Contains("Say why", await admin.SubmitAsync("/Admin/Payouts", $"/Admin/Payouts?handler=RefuseRequest&id={first}", ("answer", " ")));
        Assert.Contains("The merchant reads your answer", await admin.SubmitAsync("/Admin/Payouts", $"/Admin/Payouts?handler=RefuseRequest&id={first}", ("answer", "The rider has not handed the cash in yet")));
        var payments = await owner.PageAsync("/Merchant/Payments");
        Assert.Contains("The courier did not pay your request", payments);
        Assert.Contains("The rider has not handed the cash in yet", payments);
        Assert.Contains("did not pay your request early: The rider has not handed the cash in yet", await owner.PageAsync("/Merchant/Parcels"));
        Assert.Empty(await QueryAsync("rival", db => db.Payouts.Where(p => p.MerchantId == shop.Id).ToListAsync(Cancel)));

        // Asked again and taken back before the admin answers: the admin can no longer pay it
        await owner.SubmitAsync("/Merchant/Payments", "/Merchant/Payments?handler=Ask");
        var second = await QueryAsync("rival", db => db.PayoutRequests.Where(r => r.MerchantId == shop.Id && r.Status == PayoutRequestStatus.Open).Select(r => r.Id).SingleAsync(Cancel));
        Assert.Contains("taken back", await owner.SubmitAsync("/Merchant/Payments", "/Merchant/Payments?handler=Withdraw"));
        Assert.Contains("not found", await owner.SubmitAsync("/Merchant/Payments", "/Merchant/Payments?handler=Withdraw"));
        Assert.Contains("answered already", await admin.SubmitAsync("/Admin/Payouts", $"/Admin/Payouts?handler=PayRequest&id={second}"));
        var statuses = await QueryAsync("rival", db => db.PayoutRequests.Where(r => r.MerchantId == shop.Id).OrderBy(r => r.Id).Select(r => r.Status).ToListAsync(Cancel));
        Assert.Equal([PayoutRequestStatus.Refused, PayoutRequestStatus.Withdrawn], statuses);

        // Another merchant sees none of it
        var other = await SignInAsync("rival", (await NewMerchantAsync("rival", "Rival Town")).Email);
        Assert.DoesNotContain("The rider has not handed the cash in yet", await other.PageAsync("/Merchant/Payments"));
    }
}
