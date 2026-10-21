using Domain.Delivery;
using Domain.Parcels;

namespace Domain.Tests;

/// <summary>Bags of parcels one hub sends another on the line-haul truck, and the hub at the other end checking them.</summary>
public class BagTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_bag_takes_parcels_for_its_hub_and_sealing_it_sends_every_one()
    {
        var bag = NewBag();
        var first = AtMirpur(id: 100);
        var second = AtMirpur(id: 101);
        var forSylhet = AtMirpur(id: 102, deliveryHub: Build.Sylhet);
        var deliveredHere = AtMirpur(id: 103, deliveryHub: Build.Mirpur);

        Assert.Equal("bag.empty", bag.Seal([], null, 42, Now).Error?.Code);
        Assert.Equal(ScanOutcome.Recorded, bag.Add(first).Value);
        Assert.Equal(ScanOutcome.Recorded, bag.Add(second).Value);
        Assert.Equal(ScanOutcome.AlreadyRecorded, bag.Add(first).Value);
        Assert.Equal("bag.otherHub", bag.Add(forSylhet).Error?.Code);
        Assert.Equal("bag.otherHub", bag.Add(deliveredHere).Error?.Code);
        Assert.Equal((ParcelStatus.AtHub, Build.Mirpur), (first.Status, first.CurrentHubId));

        Assert.Equal("bag.vehicle", bag.Seal([first, second], new string('x', Bag.MaxVehicleLength + 1), 42, Now).Error?.Code);
        Assert.True(bag.Seal([first, second], " Dhaka Metro-Ta 11-2233 ", 42, Now).IsSuccess);

        Assert.Equal((BagStatus.Sealed, "Dhaka Metro-Ta 11-2233", 42L, Now), (bag.Status, bag.Vehicle, bag.SealedById, bag.SealedOn));
        Assert.Equal((ParcelStatus.InTransit, null, Build.Gulshan), (first.Status, first.CurrentHubId, first.TransferToHubId));
        Assert.Equal("Sent to the delivery hub in bag BG-100001", first.Events[^1].Note);
        Assert.Equal("bag.sealed", bag.Add(AtMirpur(id: 104)).Error?.Code);
        Assert.Equal("bag.sealed", bag.TakeOut(first).Error?.Code);
    }

    [Fact]
    public void A_parcel_that_stopped_going_the_bags_way_holds_the_seal_until_it_is_taken_out()
    {
        var bag = NewBag();
        var going = AtMirpur(id: 100);
        var sentBack = AtMirpur(id: 101);
        bag.Add(going);
        bag.Add(sentBack);
        sentBack.RequestReturn("Merchant asked for it back");

        Assert.Equal("bag.changed", bag.Seal([going, sentBack], null, 42, Now).Error?.Code);
        Assert.Equal((ParcelStatus.AtHub, Build.Mirpur), (going.Status, going.CurrentHubId));

        Assert.True(bag.TakeOut(sentBack).IsSuccess);
        Assert.Equal("bag.notInside", bag.TakeOut(sentBack).Error?.Code);
        Assert.True(bag.Seal([going], null, 42, Now).IsSuccess);
        Assert.Equal((ParcelStatus.Returning, Build.Mirpur), (sentBack.Status, sentBack.CurrentHubId));
    }

    [Fact]
    public void The_hub_it_is_for_opens_it_and_closing_it_flags_every_parcel_not_found_as_missing()
    {
        var bag = NewBag();
        var found = AtMirpur(id: 100);
        var lost = AtMirpur(id: 101);
        bag.Add(found);
        bag.Add(lost);

        Assert.Equal("bag.open", bag.Arrive(Build.Gulshan, Now).Error?.Code);
        bag.Seal([found, lost], null, 42, Now);
        Assert.Equal("bag.wrongHub", bag.Arrive(Build.Sylhet, Now).Error?.Code);
        Assert.Equal(ScanOutcome.Recorded, bag.Arrive(Build.Gulshan, Now).Value);
        Assert.Equal(ScanOutcome.AlreadyRecorded, bag.Arrive(Build.Gulshan, Now).Value);
        Assert.Equal(BagStatus.Arrived, bag.Status);

        found.ReceiveAt(Build.Gulshan, Build.Today);
        Assert.False(bag.Received(found.Id, Build.Gulshan, Now));
        Assert.False(bag.Received(found.Id, Build.Gulshan, Now));

        Assert.Equal("bag.wrongHub", bag.Close(Build.Mirpur, [lost], "who", 43, Now).Error?.Code);
        Assert.True(bag.Close(Build.Gulshan, [lost], "sealed at Mirpur hub by Karim, not found at Gulshan hub by Rahim", 43, Now).IsSuccess);

        Assert.Equal((BagStatus.Closed, 43L, Now), (bag.Status, bag.ClosedById, bag.ClosedOn));
        Assert.Equal(ParcelIssue.Exceptional, lost.Issue);
        Assert.Equal("Missing from bag BG-100001: sealed at Mirpur hub by Karim, not found at Gulshan hub by Rahim", lost.IssueNote);
        Assert.Equal((true, false), (bag.Parcels.Single(l => l.ParcelId == lost.Id).Missing, bag.Parcels.Single(l => l.ParcelId == found.Id).Missing));
        Assert.Null(found.Issue);
        Assert.Equal("bag.notOnItsWay", bag.Close(Build.Gulshan, [], "who", 43, Now).Error?.Code);
    }

    [Fact]
    public void The_last_parcel_scanned_in_closes_the_bag_by_itself()
    {
        var bag = NewBag();
        var first = AtMirpur(id: 100);
        var second = AtMirpur(id: 101);
        bag.Add(first);
        bag.Add(second);
        bag.Seal([first, second], null, 42, Now);

        Assert.False(bag.Received(first.Id, Build.Gulshan, Now));
        Assert.Equal(BagStatus.Arrived, bag.Status);
        Assert.True(bag.Received(second.Id, Build.Gulshan, Now));

        Assert.Equal((BagStatus.Closed, (long?)null), (bag.Status, bag.ClosedById));
        Assert.All(bag.Parcels, line => Assert.Equal(Now, line.ReceivedOn));
    }

    private static Bag NewBag()
    {
        var bag = Build.WithId(Bag.Open(Build.Mirpur, Build.Gulshan), 1);
        typeof(Bag).GetProperty(nameof(Bag.Number))!.SetValue(bag, "BG-100001");

        return bag;
    }

    /// <summary>A parcel collected by Mirpur and scanned in there, for <paramref name="deliveryHub"/>.</summary>
    private static Parcel AtMirpur(long id, long deliveryHub = Build.Gulshan)
    {
        var parcel = Build.Parcel(deliveryHub: deliveryHub, id: id);
        parcel.PickUp(Build.Today);
        parcel.ReceiveAt(Build.Mirpur, Build.Today);

        return parcel;
    }
}
