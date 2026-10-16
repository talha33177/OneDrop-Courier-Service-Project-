using Domain.Delivery;

namespace Domain.Tests;

/// <summary>Riders, their daily runs and delivery attempts, and pickup requests.</summary>
public class DeliveryTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 10, 4);

    private static Rider NewRider(long hubId = Build.Mirpur, long id = 5, Vehicle vehicle = Vehicle.Motorbike)
    {
        return Build.WithId(Rider.Create(hubId, "Rafiq Hasan", "01722000001", vehicle, userId: 30).Value, id);
    }

    /// <summary>A rider on the only vehicle that collects from merchants.</summary>
    private static Rider NewVan(long hubId = Build.Mirpur, long id = 6)
    {
        return Build.WithId(Rider.Create(hubId, "Shahin Mollah", "01722000007", Vehicle.Van, userId: 31).Value, id);
    }

    private static readonly Load Empty = Load.Of([]);

    [Fact]
    public void A_rider_needs_a_name_a_mobile_number_and_a_vehicle()
    {
        Assert.Equal("rider.name", Rider.Create(1, " ", "01722000001", Vehicle.Motorbike, null).Error?.Code);
        Assert.Equal("phone.invalid", Rider.Create(1, "Rafiq", "12345", Vehicle.Motorbike, null).Error?.Code);
        Assert.Equal("rider.vehicle", Rider.Create(1, "Rafiq", "01722000001", default, null).Error?.Code);
        Assert.Equal("+8801722000001", NewRider().Phone);
        Assert.Equal(Vehicle.Bicycle, NewRider(vehicle: Vehicle.Bicycle).Vehicle);
    }

    [Fact]
    public void A_run_takes_parcels_while_open_and_records_each_outcome_once()
    {
        var run = Build.WithId(DeliveryRun.Open(NewRider(), Today), 9);
        var parcel = Build.OutForDelivery();
        var attempt = run.Add(parcel, Now).Value;

        Assert.Equal(run.Id, attempt.RunId);
        Assert.Equal(5, attempt.RiderId);
        Assert.Equal(7, attempt.MerchantId);
        Assert.False(attempt.IsDone);

        Assert.True(attempt.Complete(AttemptOutcome.Delivered, 1250, null, Now).IsSuccess);
        Assert.Equal("attempt.done", attempt.Complete(AttemptOutcome.Hold, 0, "x", Now).Error?.Code);
        Assert.Throws<InvalidOperationException>(() => run.Add(parcel, Now).Value.Complete(AttemptOutcome.Hold, 100, "x", Now));
    }

    [Fact]
    public void Closing_a_run_records_the_cash_and_any_shortfall_once()
    {
        var run = Build.WithId(DeliveryRun.Open(NewRider(), Today), 9);

        Assert.Equal("run.cash", run.Close(2085, -1, Now).Error?.Code);
        Assert.True(run.Close(2085, 2000, Now).IsSuccess);
        Assert.Equal(85, run.CashShort);
        Assert.Equal(RunStatus.Closed, run.Status);
        Assert.Equal("run.closed", run.Close(2085, 2085, Now).Error?.Code);
        Assert.Equal("run.closed", run.Add(Build.OutForDelivery(), Now).Error?.Code);
    }

    [Fact]
    public void A_pickup_is_scheduled_for_its_day_and_goes_scheduled_assigned_completed()
    {
        var pickup = PickupRequest.Schedule(7, 1, Build.Mirpur, Today);
        var load = Load.Of([500, 1_000, 800]);
        Assert.Equal(PickupStatus.Scheduled, pickup.Status);
        Assert.Equal(Today, pickup.PickupDate);
        Assert.Equal("pickup.notAssigned", pickup.Complete(3, Now).Error?.Code);
        Assert.Equal("pickup.rider", pickup.Assign(NewVan(hubId: Build.Gulshan), Build.Fleet(), Empty, load).Error?.Code);
        Assert.True(pickup.Assign(NewVan(), Build.Fleet(), Empty, load).IsSuccess);
        Assert.True(pickup.Complete(2, Now).IsSuccess);

        Assert.Equal(PickupStatus.Completed, pickup.Status);
        Assert.Equal(2, pickup.PickedParcels);
        Assert.Equal("pickup.closed", pickup.Cancel().Error?.Code);
        Assert.Equal("pickup.carry", pickup.CarryTo(Today.AddDays(1)).Error?.Code);
    }

    [Fact]
    public void A_pickup_whose_day_passed_is_carried_to_today_and_waits_for_a_rider_again()
    {
        var pickup = PickupRequest.Schedule(7, 1, Build.Mirpur, Today.AddDays(-1));
        Assert.True(pickup.Assign(NewVan(), Build.Fleet(), Empty, Load.Of([500])).IsSuccess);

        Assert.Equal("pickup.carry", pickup.CarryTo(Today.AddDays(-1)).Error?.Code);
        Assert.True(pickup.CarryTo(Today).IsSuccess);
        Assert.Equal(Today, pickup.PickupDate);
        Assert.Equal(PickupStatus.Scheduled, pickup.Status);
        Assert.Null(pickup.RiderId);
    }

    [Fact]
    public void A_stopped_rider_is_not_sent_on_a_pickup()
    {
        var rider = NewVan();
        rider.Deactivate();
        var pickup = PickupRequest.Schedule(7, 1, Build.Mirpur, Today);

        Assert.Equal("pickup.rider", pickup.Assign(rider, Build.Fleet(), Empty, Load.Of([1_000])).Error?.Code);
        Assert.True(pickup.Cancel().IsSuccess);
        Assert.Equal(PickupStatus.Cancelled, pickup.Status);
    }

    [Theory]
    [InlineData(0, 30_000, 10_000, "capacity.parcels")]
    [InlineData(40, 500, 100, "capacity.load")]
    [InlineData(40, 30_000, 50, "capacity.parcel")]
    [InlineData(40, 30_000, 31_000, "capacity.parcel")]
    public void A_vehicle_carries_at_least_one_parcel_and_a_parcel_no_heavier_than_the_load(int parcels, int load, int heaviest, string code)
    {
        Assert.Equal(code, VehicleCapacity.Create(Vehicle.Motorbike, new CapacityValues(parcels, load, heaviest)).Error?.Code);
    }

    [Fact]
    public void A_rider_takes_parcels_until_their_vehicle_is_full_in_number_or_weight()
    {
        var motorbike = Build.Fleet()[Vehicle.Motorbike]!;

        Assert.True(motorbike.Take(new Load(39, 20_000, 2_000), "OD1", 1_000).IsSuccess);
        Assert.Equal("capacity.parcels", motorbike.Take(new Load(40, 20_000, 2_000), "OD1", 1_000).Error?.Code);
        Assert.Equal("capacity.load", motorbike.Take(new Load(10, 29_500, 2_000), "OD1", 1_000).Error?.Code);
        var heavy = motorbike.Take(new Load(0, 0, 0), "OD1", 12_000).Error!;
        Assert.Equal("capacity.parcel", heavy.Code);
        Assert.Equal("OD1 weighs 12 kg; a motorbike takes parcels up to 10 kg.", heavy.Message);
    }

    [Fact]
    public void A_load_adds_up_its_parcels_and_keeps_the_heaviest()
    {
        var load = Load.Of([500, 2_000, 1_200]).With(300);

        Assert.Equal(new Load(4, 4_000, 2_000), load);
        Assert.Equal(new Load(0, 0, 0), Load.Of([]));
    }

    [Fact]
    public void A_vehicle_with_no_capacity_set_takes_anything()
    {
        var fleet = new Fleet([]);

        Assert.True(fleet.Take(Vehicle.Bicycle, new Load(500, 900_000, 30_000), "OD1", 30_000).IsSuccess);
        Assert.True(fleet.Collect(Vehicle.Van, new Load(900, 0, 0), new Load(900, 0, 0)).IsSuccess);
    }

    [Fact]
    public void Only_a_pickup_van_collects_from_merchants()
    {
        var pickup = PickupRequest.Schedule(7, 1, Build.Mirpur, Today);
        var load = Load.Of([500, 1_000]);

        foreach (var vehicle in new[] { Vehicle.Bicycle, Vehicle.Motorbike })
        {
            var refused = pickup.Assign(NewRider(vehicle: vehicle), Build.Fleet(), Empty, load).Error!;
            Assert.Equal("pickup.vehicle", refused.Code);
            Assert.Contains("does not collect from merchants", refused.Message);
            Assert.Null(pickup.RiderId);
        }

        Assert.True(pickup.Assign(NewVan(), Build.Fleet(), Empty, load).IsSuccess);
        Assert.False(Vehicle.Bicycle.CollectsPickups());
        Assert.True(Vehicle.Van.CollectsPickups());
    }

    [Fact]
    public void A_pickup_is_refused_when_the_van_is_already_too_full_for_it()
    {
        var pickup = PickupRequest.Schedule(7, 1, Build.Mirpur, Today);
        // The van takes 300 parcels or 1,000 kg: 250 on board already leaves room for 50
        var onBoard = Load.Of([.. Enumerable.Repeat(1_000, 250)]);

        var refused = pickup.Assign(NewVan(), Build.Fleet(), onBoard, Load.Of([.. Enumerable.Repeat(1_000, 80)])).Error!;
        Assert.Equal("pickup.full", refused.Code);
        Assert.Equal(
            "With this pickup the pickup van would have 330 parcels; it takes 300 at once. " +
            "Give it to another pickup van, or after this one has unloaded at the hub.",
            refused.Message);
        Assert.Null(pickup.RiderId);

        Assert.True(pickup.Assign(NewVan(), Build.Fleet(), onBoard, Load.Of([.. Enumerable.Repeat(1_000, 50)])).IsSuccess);
    }

    [Fact]
    public void A_pickup_bigger_than_the_whole_van_goes_to_an_empty_van_in_more_than_one_trip()
    {
        var pickup = PickupRequest.Schedule(7, 1, Build.Mirpur, Today);
        var load = Load.Of([.. Enumerable.Repeat(100, 1_000)]);
        var fleet = Build.Fleet();

        Assert.True(pickup.Assign(NewVan(), fleet, Empty, load).IsSuccess);
        Assert.Equal(4, fleet.Trips(Vehicle.Van, load));
        Assert.Equal(1, fleet.Trips(Vehicle.Van, Load.Of([500, 900])));

        // Not once the van already has something on it: that load would be stranded
        var second = PickupRequest.Schedule(7, 2, Build.Mirpur, Today);
        Assert.Equal("pickup.full", second.Assign(NewVan(), fleet, Load.Of([500]), load).Error?.Code);
    }

    [Fact]
    public void A_parcel_heavier_than_the_vehicle_takes_is_refused_whatever_room_is_left()
    {
        var fleet = Build.Fleet();

        Assert.Equal("capacity.parcel", fleet.Take(Vehicle.Bicycle, Empty, "OD1", 6_000).Error?.Code);
        Assert.True(fleet.Take(Vehicle.Motorbike, Empty, "OD1", 6_000).IsSuccess);
    }
}
