using System.Net;
using Microsoft.EntityFrameworkCore;
using Domain.Delivery;
using Domain.Parcels;

namespace Integration.Tests;

/// <summary>
/// Bags between hubs through the pages: line-haul at Chattogram bags parcels per hub on the scanner, takes one out and
/// seals a bag; Sylhet scans the bag and a parcel in and closes it, which flags the parcel it did not find. Chattogram
/// and Sylhet are used by no other test, so no one else's parcels share these bags.
/// </summary>
public class HubBagsTests(WebAppFactory factory) : AppTests(factory)
{
    [Fact]
    public async Task Line_haul_bags_parcels_per_hub_and_the_hub_at_the_other_end_flags_what_was_not_in_the_bag()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: "Agrabad");
        var found = await BookAsync(shop.ApiKey, area: "Sylhet Sadar");
        var lost = await BookAsync(shop.ApiKey, area: "Sylhet Sadar");
        var kept = await BookAsync(shop.ApiKey, area: "Sylhet Sadar");
        var rajshahi = await BookAsync(shop.ApiKey, area: "Rajshahi Sadar");
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        foreach (var code in new[] { found, lost, kept, rajshahi })
        {
            await hub.SubmitAsync("/Hub/Scan?hub=CTG", "/Hub/Scan?mode=Receive&hub=CTG", ("Code", code));
        }

        // Each parcel goes in the bag for the hub it travels to next; scanning one twice changes nothing
        foreach (var code in new[] { found, lost, kept })
        {
            Assert.Contains("Bag for Sylhet hub", await hub.SubmitAsync("/Hub/Scan?hub=CTG", "/Hub/Scan?mode=Dispatch&hub=CTG", ("Code", code)));
        }

        Assert.Contains("Already in the bag for Sylhet hub", await hub.SubmitAsync("/Hub/Scan?hub=CTG", "/Hub/Scan?mode=Dispatch&hub=CTG", ("Code", found)));
        Assert.Contains("Bag for Rajshahi hub", await hub.SubmitAsync("/Hub/Scan?hub=CTG", "/Hub/Scan?mode=Dispatch&hub=CTG", ("Code", rajshahi)));
        var number = await BagOfAsync(found);
        Assert.NotEqual(number, await BagOfAsync(rajshahi));
        var page = await hub.PageAsync("/Hub/Bags?hub=CTG");
        Assert.Contains(number, page);
        Assert.Contains(kept, page);

        // One comes out before sealing and stays at the hub; sealing sends the others
        Assert.Contains(
            $"{kept} is out of bag {number}",
            await hub.SubmitAsync("/Hub/Bags?hub=CTG", "/Hub/Bags?handler=TakeOut&hub=CTG", ("number", number), ("code", kept)));
        Assert.Contains(
            $"Bag {number} is sealed",
            await hub.SubmitAsync("/Hub/Bags?hub=CTG", "/Hub/Bags?handler=Seal&hub=CTG", ("number", number), ("vehicle", "Truck CTG-11, driver Babul")));
        Assert.Equal((ParcelStatus.InTransit, ParcelStatus.InTransit, ParcelStatus.AtHub), ((await ParcelAsync(found)).Status, (await ParcelAsync(lost)).Status, (await ParcelAsync(kept)).Status));
        Assert.Contains($"in bag {number}", await hub.PageAsync($"/Hub/Parcel/{found}"));
        var manifest = await hub.PageAsync($"/Hub/Bag/{number}?hub=CTG");
        Assert.Contains("Truck CTG-11, driver Babul", manifest);
        Assert.Contains(lost, manifest);
        Assert.DoesNotContain(kept, manifest);

        // Sylhet scans the bag, then the parcel it finds, and closes the bag: the rest are flagged missing. The bag
        // holds two parcels of this test, and more when an earlier run stopped before sealing its bag
        var inside = await QueryAsync("onedrop", db => db.BagParcels.CountAsync(line => db.Bags.Any(b => b.Id == line.BagId && b.Number == number), Cancel));
        Assert.True(inside >= 2);
        Assert.Contains("is for another hub", await hub.SubmitAsync("/Hub/Scan?hub=RAJ", "/Hub/Scan?mode=Receive&hub=RAJ", ("Code", number)));
        var opened = await hub.SubmitAsync("/Hub/Scan?hub=SYL", "/Hub/Scan?mode=Receive&hub=SYL", ("Code", number.ToLowerInvariant()));
        Assert.Contains($"Bag {number} from Chattogram hub", opened);
        Assert.Contains($"{inside} parcels inside", opened);
        Assert.Contains($"Bag {number}: 1 of {inside} scanned in", await hub.SubmitAsync("/Hub/Scan?hub=SYL", "/Hub/Scan?mode=Receive&hub=SYL", ("Code", found)));
        Assert.Contains(
            inside == 2 ? $"Bag {number} is closed. 1 parcel was not in it" : $"Bag {number} is closed. {inside - 1} parcels were not in it",
            await hub.SubmitAsync("/Hub/Bags?hub=SYL", "/Hub/Bags?handler=Close&hub=SYL", ("number", number)));

        var missing = await ParcelAsync(lost);
        Assert.Equal((ParcelIssue.Exceptional, ParcelStatus.InTransit), (missing.Issue, missing.Status));
        Assert.Equal($"Missing from bag {number}: sealed at Chattogram hub by Hub staff, not found at Sylhet hub by Hub staff", missing.IssueNote);
        Assert.Null((await ParcelAsync(found)).Issue);
        var bag = await QueryAsync("onedrop", db => db.Bags.Include(b => b.Parcels).SingleAsync(b => b.Number == number, Cancel));
        Assert.Equal(BagStatus.Closed, bag.Status);
        Assert.Equal(inside - 1, bag.Parcels.Count(line => line.Missing));
    }

    [Fact]
    public async Task Only_line_haul_seals_only_receiving_closes_and_a_hubs_staff_see_only_their_hubs_bags()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: "Agrabad");
        var code = await BookAsync(shop.ApiKey, area: "Khulna Sadar");
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        await hub.SubmitAsync("/Hub/Scan?hub=CTG", "/Hub/Scan?mode=Receive&hub=CTG", ("Code", code));
        await hub.SubmitAsync("/Hub/Scan?hub=CTG", "/Hub/Scan?mode=Dispatch&hub=CTG", ("Code", code));
        var number = await BagOfAsync(code);

        // A receiving clerk at Mirpur: the bags page opens at their own hub, but not this bag, and sealing is refused
        var clerk = await SignInAsync("onedrop", "receiving@onedrop.test");
        Assert.Equal(HttpStatusCode.OK, (await clerk.GetAsync("/Hub/Bags")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clerk.GetAsync($"/Hub/Bag/{number}")).StatusCode);
        var seal = await clerk.PostFormAsync("/Hub/Bags", "/Hub/Bags?handler=Seal&hub=MIR", ("number", number));
        Assert.NotEqual(HttpStatusCode.OK, seal.StatusCode);
        Assert.Equal(BagStatus.Open, await QueryAsync("onedrop", db => db.Bags.Where(b => b.Number == number).Select(b => b.Status).SingleAsync(Cancel)));

        // Accounts at head office has no desk at a hub
        var accounts = await SignInAsync("onedrop", "accounts@onedrop.test");
        Assert.NotEqual(HttpStatusCode.OK, (await accounts.GetAsync("/Hub/Bags?hub=CTG")).StatusCode);

        // Sealing it from a hub it does not leave is not found
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await hub.PostFormAsync("/Hub/Bags?hub=SYL", "/Hub/Bags?handler=Seal&hub=SYL", ("number", number))).StatusCode);
        Assert.Contains($"Bag {number} is sealed", await hub.SubmitAsync("/Hub/Bags?hub=CTG", "/Hub/Bags?handler=Seal&hub=CTG", ("number", number)));
    }

    private Task<string> BagOfAsync(string code)
    {
        return QueryAsync("onedrop", db => (
            from line in db.BagParcels
            join bag in db.Bags on line.BagId equals bag.Id
            join parcel in db.Parcels on line.ParcelId equals parcel.Id
            where parcel.TrackingCode == code && bag.Status == BagStatus.Open
            select bag.Number).SingleAsync(Cancel));
    }
}
