using System.Net;
using Microsoft.EntityFrameworkCore;
using Domain.Delivery;
using Domain.Parcels;
using Domain.Payments;

namespace Integration.Tests;

/// <summary>
/// One parcel's whole life through the panels' pages, as the people who do the work see it: the merchant books it,
/// which puts it on the day's pickup list, the hub sends a rider, scans it in, sends it to the hub that delivers it, hands it to a rider, the
/// rider delivers it and the hub closes the rider's run. Then the ledger, the merchant's pages and public tracking.
/// </summary>
public class DeliveryFlowTests(WebAppFactory factory) : AppTests(factory)
{
    [Fact]
    public async Task A_parcel_goes_from_the_merchant_through_two_hubs_to_the_door_and_the_cash_is_accounted_for()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: "Mirpur 10");
        var collector = await NewRiderAsync("MIR", vehicle: Vehicle.Van);
        var deliverer = await NewRiderAsync("GUL");
        var code = await BookAsync(shop.ApiKey, area: "Gulshan 2", cod: 1250, weightKg: 0.8m);
        var merchant = await SignInAsync("onedrop", shop.Email);
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");

        // Booking put the merchant's pickup point on the day's pickup list; the Mirpur hub sends a rider who collects it
        var pickupId = await QueryAsync("onedrop", db => db.PickupRequests.Where(r => r.MerchantId == shop.Id).Select(r => r.Id).SingleAsync(Cancel));
        Assert.Contains("Waiting for a rider", await merchant.PageAsync("/Merchant/Pickups"));
        Assert.Contains("Rider assigned", await hub.SubmitAsync("/Hub/Pickups?hub=MIR", $"/Hub/Pickups?handler=Assign&id={pickupId}&hub=MIR", ("riderId", $"{collector.Id}")));
        var rider = await SignInAsync("onedrop", collector.Email);
        Assert.Contains(code, await rider.PageAsync("/Rider/Pickups"));
        Assert.Contains(
            "1 parcel collected",
            await rider.SubmitAsync("/Rider/Pickups", $"/Rider/Pickups?id={pickupId}", ("codes", code), ("shown", code)));
        Assert.Equal(ParcelStatus.PickedUp, (await ParcelAsync(code)).Status);

        // Mirpur receives it and bags it for Gulshan; the sealed bag travels, and Gulshan scans the parcel in, which
        // closes the bag when it is the last one, and hands it to its rider
        Assert.Contains("Send it to Gulshan hub", await hub.SubmitAsync("/Hub/Scan?hub=MIR", "/Hub/Scan?mode=Receive&hub=MIR", ("Code", code.ToLowerInvariant())));
        Assert.Contains("Already scanned in here", await hub.SubmitAsync("/Hub/Scan?hub=MIR", "/Hub/Scan?mode=Receive&hub=MIR", ("Code", code)));
        Assert.Contains("Bag for Gulshan hub", await hub.SubmitAsync("/Hub/Scan?hub=MIR", "/Hub/Scan?mode=Dispatch&hub=MIR", ("Code", code)));
        Assert.Equal(ParcelStatus.AtHub, (await ParcelAsync(code)).Status);
        var bag = await SealBagOfAsync(hub, "MIR", code);
        Assert.Equal(ParcelStatus.InTransit, (await ParcelAsync(code)).Status);
        Assert.Contains("Already on its way to Gulshan hub", await hub.SubmitAsync("/Hub/Scan?hub=MIR", "/Hub/Scan?mode=Dispatch&hub=MIR", ("Code", code)));
        Assert.Contains("assign it to a rider", await hub.SubmitAsync("/Hub/Scan?hub=GUL", "/Hub/Scan?mode=Receive&hub=GUL", ("Code", code)));
        var parcelId = (await ParcelAsync(code)).Id;
        var arrived = await QueryAsync("onedrop", db => db.Bags.Include(b => b.Parcels).SingleAsync(b => b.Number == bag, Cancel));
        Assert.NotNull(arrived.Parcels.Single(line => line.ParcelId == parcelId).ReceivedOn);
        Assert.Contains(code, await hub.PageAsync("/Hub/Assign?hub=GUL"));
        Assert.Contains("1 parcel handed over", await hub.SubmitAsync("/Hub/Assign?hub=GUL", "/Hub/Assign?hub=GUL", ("riderId", $"{deliverer.Id}"), ("codes", code)));

        // The rider delivers it and collects the cash
        var door = await SignInAsync("onedrop", deliverer.Email);
        Assert.Contains(code, await door.PageAsync("/Rider"));
        Assert.Contains($"Delivered: {code}", await door.SubmitAsync($"/Rider/Delivery/{code}", $"/Rider/Delivery/{code}?handler=Deliver", ("collected", "1250"), ("reason", "")));
        var delivered = await ParcelAsync(code);
        Assert.Equal((ParcelStatus.Delivered, 1250m, 13m), (delivered.Status, delivered.CollectedAmount!.Value, delivered.CodCharge!.Value));
        Assert.Null(delivered.RiderId);

        // The merchant is owed the cash less the delivery and COD charges
        var lines = await QueryAsync("onedrop", db => db.LedgerEntries.Where(e => e.ParcelId == delivered.Id).OrderBy(e => e.Kind).Select(e => new { e.Kind, e.Amount }).ToListAsync(Cancel));
        Assert.Equal(
            [(LedgerEntryKind.Cod, 1250m), (LedgerEntryKind.DeliveryCharge, -60m), (LedgerEntryKind.CodCharge, -13m)],
            lines.Select(l => (l.Kind, l.Amount)));

        // The Gulshan hub closes the rider's run with the cash handed in, ৳50 short
        var runId = await QueryAsync("onedrop", db => db.DeliveryRuns.Where(r => r.RiderId == deliverer.Id).Select(r => r.Id).SingleAsync(Cancel));
        Assert.Contains("Run closed", await hub.SubmitAsync("/Hub/Runs?hub=GUL", $"/Hub/Runs?handler=Close&id={runId}&hub=GUL", ("received", "1200")));
        var run = await QueryAsync("onedrop", db => db.DeliveryRuns.SingleAsync(r => r.Id == runId, Cancel));
        Assert.Equal((RunStatus.Closed, 1250m, 1200m), (run.Status, run.CashExpected!.Value, run.CashReceived!.Value));

        // The merchant sees it delivered and the money waiting; the public tracking page shows the way it came
        Assert.Contains("Delivered", await merchant.PageAsync($"/Merchant/Parcel/{code}"));
        var payments = await merchant.PageAsync("/Merchant/Payments");
        Assert.Contains(code, payments);
        Assert.Contains("৳1,177", payments);
        var tracking = await Visit("onedrop").PageAsync($"/Track?code={code}");
        Assert.Contains("Delivered", tracking);
        Assert.Contains("Gulshan hub", tracking);
        Assert.Contains(shop.Name, tracking);
        Assert.DoesNotContain("House 22", tracking);
        Assert.DoesNotContain("1,250", tracking);
    }

    [Fact]
    public async Task A_refused_parcel_goes_back_to_the_hub_that_collected_it_and_costs_the_delivery_and_return_charges()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: "Mirpur 10");
        var deliverer = await NewRiderAsync("SAV");
        var code = await BookAsync(shop.ApiKey, area: "Savar", cod: 900);
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");

        await hub.SubmitAsync("/Hub/Scan?hub=MIR", "/Hub/Scan?mode=Receive&hub=MIR", ("Code", code));
        await hub.SubmitAsync("/Hub/Scan?hub=MIR", "/Hub/Scan?mode=Dispatch&hub=MIR", ("Code", code));
        await SealBagOfAsync(hub, "MIR", code);
        await hub.SubmitAsync("/Hub/Scan?hub=SAV", "/Hub/Scan?mode=Receive&hub=SAV", ("Code", code));
        await hub.SubmitAsync("/Hub/Assign?hub=SAV", "/Hub/Assign?hub=SAV", ("riderId", $"{deliverer.Id}"), ("codes", code));
        var rider = await SignInAsync("onedrop", deliverer.Email);
        Assert.Contains("Marked refused", await rider.SubmitAsync($"/Rider/Delivery/{code}", $"/Rider/Delivery/{code}?handler=Refuse", ("reason", "Customer refused the parcel")));
        Assert.Equal(ParcelStatus.Returning, (await ParcelAsync(code)).Status);

        // Closing the run takes the parcel back at Savar; it travels to Mirpur, which hands it back
        var runId = await QueryAsync("onedrop", db => db.DeliveryRuns.Where(r => r.RiderId == deliverer.Id).Select(r => r.Id).SingleAsync(Cancel));
        Assert.Contains("Run closed", await hub.SubmitAsync("/Hub/Runs?hub=SAV", $"/Hub/Runs?handler=Close&id={runId}&hub=SAV", ("received", "0")));
        Assert.Equal(await QueryAsync("onedrop", db => db.Hubs.Where(h => h.Code == "SAV").Select(h => (long?)h.Id).SingleAsync(Cancel)), (await ParcelAsync(code)).CurrentHubId);
        Assert.Contains("goes back to its merchant from another hub", await hub.SubmitAsync("/Hub/Scan?hub=SAV", "/Hub/Scan?mode=HandBack&hub=SAV", ("Code", code)));
        Assert.Contains("Bag for Mirpur hub", await hub.SubmitAsync("/Hub/Scan?hub=SAV", "/Hub/Scan?mode=Dispatch&hub=SAV", ("Code", code)));
        await SealBagOfAsync(hub, "SAV", code);
        Assert.Contains($"hand it back to {shop.Name}", await hub.SubmitAsync("/Hub/Scan?hub=MIR", "/Hub/Scan?mode=Receive&hub=MIR", ("Code", code)));
        Assert.Contains($"Returned to {shop.Name}", await hub.SubmitAsync("/Hub/Scan?hub=MIR", "/Hub/Scan?mode=HandBack&hub=MIR", ("Code", code)));

        var returned = await ParcelAsync(code);
        Assert.Equal(ParcelStatus.Returned, returned.Status);
        var lines = await QueryAsync("onedrop", db => db.LedgerEntries.Where(e => e.ParcelId == returned.Id).OrderBy(e => e.Kind).Select(e => new { e.Kind, e.Amount }).ToListAsync(Cancel));
        Assert.Equal([(LedgerEntryKind.DeliveryCharge, -100m), (LedgerEntryKind.ReturnCharge, -50m)], lines.Select(l => (l.Kind, l.Amount)));
    }

    [Fact]
    public async Task A_parcel_is_held_until_its_last_attempt_and_the_run_does_not_close_while_a_parcel_has_no_outcome()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: "Uttara Sector 7");
        var deliverer = await NewRiderAsync("UTT");
        var code = await BookAsync(shop.ApiKey, area: "Uttara Sector 4", cod: 500);
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        var rider = await SignInAsync("onedrop", deliverer.Email);
        await hub.SubmitAsync("/Hub/Scan?hub=UTT", "/Hub/Scan?mode=Receive&hub=UTT", ("Code", code));
        await hub.SubmitAsync("/Hub/Assign?hub=UTT", "/Hub/Assign?hub=UTT", ("riderId", $"{deliverer.Id}"), ("codes", code));
        var runId = await QueryAsync("onedrop", db => db.DeliveryRuns.Where(r => r.RiderId == deliverer.Id).Select(r => r.Id).SingleAsync(Cancel));

        Assert.Contains("has not recorded what happened to 1 parcel", await hub.SubmitAsync("/Hub/Runs?hub=UTT", $"/Hub/Runs?handler=Close&id={runId}&hub=UTT", ("received", "0")));

        // Two attempts end on hold (the launch courier allows three); the third must be delivered or returned
        Assert.Contains("Put on hold", await rider.SubmitAsync($"/Rider/Delivery/{code}", $"/Rider/Delivery/{code}?handler=Hold", ("reason", "Customer not reachable"), ("until", "")));
        await hub.SubmitAsync("/Hub/Scan?hub=UTT", "/Hub/Scan?mode=Receive&hub=UTT", ("Code", code));
        await hub.SubmitAsync("/Hub/Assign?hub=UTT", "/Hub/Assign?hub=UTT", ("riderId", $"{deliverer.Id}"), ("codes", code));
        Assert.Contains("Put on hold", await rider.SubmitAsync($"/Rider/Delivery/{code}", $"/Rider/Delivery/{code}?handler=Hold", ("reason", "Customer asked for another day"), ("until", "")));
        await hub.SubmitAsync("/Hub/Scan?hub=UTT", "/Hub/Scan?mode=Receive&hub=UTT", ("Code", code));
        await hub.SubmitAsync("/Hub/Assign?hub=UTT", "/Hub/Assign?hub=UTT", ("riderId", $"{deliverer.Id}"), ("codes", code));

        var page = await rider.PageAsync($"/Rider/Delivery/{code}");
        Assert.Contains("last attempt", page);
        Assert.DoesNotContain("Not today (hold)", page);
        Assert.Contains("attempt 3 of 3", await rider.SubmitAsync($"/Rider/Delivery/{code}", $"/Rider/Delivery/{code}?handler=Hold", ("reason", "Again"), ("until", "")));
        Assert.Contains($"Delivered: {code}", await rider.SubmitAsync($"/Rider/Delivery/{code}", $"/Rider/Delivery/{code}?handler=Deliver", ("collected", "300"), ("reason", "Kept one item")));

        var parcel = await ParcelAsync(code);
        Assert.Equal((ParcelStatus.PartlyDelivered, 3, 300m), (parcel.Status, parcel.Attempts, parcel.CollectedAmount!.Value));
    }

    [Fact]
    public async Task Another_hubs_parcel_is_not_assigned_here_and_a_rider_records_only_their_own_parcels()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: "Mirpur 10");
        var mirpurRider = await NewRiderAsync("MIR");
        var otherRider = await NewRiderAsync("MIR");
        var code = await BookAsync(shop.ApiKey, area: "Gulshan 1");
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        await hub.SubmitAsync("/Hub/Scan?hub=MIR", "/Hub/Scan?mode=Receive&hub=MIR", ("Code", code));

        Assert.Contains("delivered from another hub", await hub.SubmitAsync("/Hub/Assign?hub=MIR", "/Hub/Assign?hub=MIR", ("riderId", $"{mirpurRider.Id}"), ("codes", code)));

        var mirpurCode = await BookAsync(shop.ApiKey, area: "Pallabi");
        await hub.SubmitAsync("/Hub/Scan?hub=MIR", "/Hub/Scan?mode=Receive&hub=MIR", ("Code", mirpurCode));
        await hub.SubmitAsync("/Hub/Assign?hub=MIR", "/Hub/Assign?hub=MIR", ("riderId", $"{mirpurRider.Id}"), ("codes", mirpurCode));
        var other = await SignInAsync("onedrop", otherRider.Email);

        Assert.DoesNotContain(mirpurCode, await other.PageAsync("/Rider"));
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/Rider/Delivery/{mirpurCode}")).StatusCode);
        Assert.Equal(ParcelStatus.OutForDelivery, (await ParcelAsync(mirpurCode)).Status);
    }

    /// <summary>Line-haul at <paramref name="hubCode"/> seals the open bag the parcel was scanned into; returns its number.</summary>
    private async Task<string> SealBagOfAsync(Visitor hub, string hubCode, string code)
    {
        var number = await QueryAsync("onedrop", db => (
            from line in db.BagParcels
            join bag in db.Bags on line.BagId equals bag.Id
            join parcel in db.Parcels on line.ParcelId equals parcel.Id
            where parcel.TrackingCode == code && bag.Status == BagStatus.Open
            select bag.Number).SingleAsync(Cancel));
        Assert.Contains(
            $"Bag {number} is sealed",
            await hub.SubmitAsync($"/Hub/Bags?hub={hubCode}", $"/Hub/Bags?handler=Seal&hub={hubCode}", ("number", number), ("vehicle", "")));

        return number;
    }
}
