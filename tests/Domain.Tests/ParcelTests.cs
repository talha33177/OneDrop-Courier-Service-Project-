using Domain.Common;
using Domain.Parcels;
using Domain.Pricing;

namespace Domain.Tests;

/// <summary>A parcel's life: booking, pickup, the hubs, the rider, the door, and the way back to the merchant.</summary>
public class ParcelTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly Today = new(2026, 10, 4);

    [Fact]
    public void A_booked_parcel_is_pending_with_the_charges_it_was_priced_at_and_a_first_tracking_line()
    {
        var parcel = Build.Parcel(cod: 1250, grams: 2300);

        Assert.Equal(ParcelStatus.Pending, parcel.Status);
        Assert.Equal(60 + 2 * 15, parcel.DeliveryCharge);
        Assert.Equal(1, parcel.CodChargePercent);
        Assert.Equal("+8801811000101", parcel.RecipientPhone);
        Assert.Equal("Parcel booked", Assert.Single(parcel.Events).Note);
        Assert.Null(parcel.CurrentHubId);
    }

    [Theory]
    [InlineData(0, 1250, "parcel.weight")]
    [InlineData(30_001, 1250, "parcel.weight")]
    [InlineData(500, -1, "parcel.cod")]
    public void A_parcel_outside_the_limits_is_not_booked(int grams, decimal cod, string code)
    {
        var created = Parcel.Create(new NewParcel(
            7,
            1,
            Build.Mirpur,
            new ParcelDetails(1, Build.Mirpur, "Rahim", PhoneNumber.Parse("01811000101").Value, "House 1", cod, grams, null, null, Build.Rate(Pricing.ServiceArea.InsideCity).ChargesFor(500))));

        Assert.Equal(code, created.Error?.Code);
    }

    [Fact]
    public void A_parcel_is_picked_up_once_and_received_at_a_hub()
    {
        var parcel = Build.Parcel();

        Assert.Equal(ScanOutcome.Recorded, parcel.PickUp(Today).Value);
        Assert.Equal(ScanOutcome.AlreadyRecorded, parcel.PickUp(Today).Value);
        Assert.Equal(ScanOutcome.Recorded, parcel.ReceiveAt(Build.Mirpur, Today).Value);
        Assert.Equal(ScanOutcome.AlreadyRecorded, parcel.ReceiveAt(Build.Mirpur, Today).Value);

        Assert.Equal(ParcelStatus.AtHub, parcel.Status);
        Assert.Equal(Build.Mirpur, parcel.CurrentHubId);
    }

    [Fact]
    public void A_parcel_the_merchant_drops_at_the_hub_is_received_without_a_pickup()
    {
        var parcel = Build.Parcel();

        parcel.ReceiveAt(Build.Mirpur, Today);

        Assert.Equal(ParcelStatus.AtHub, parcel.Status);
        Assert.Equal("Dropped off at the hub by the merchant", parcel.Events[^1].Note);
    }

    [Fact]
    public void A_parcel_travels_to_the_hub_that_delivers_it_and_is_received_there()
    {
        var parcel = Build.Parcel(pickupHub: Build.Mirpur, deliveryHub: Build.Sylhet);
        parcel.ReceiveAt(Build.Mirpur, Today);

        Assert.Equal("parcel.assign.otherHub", parcel.AssignTo(5, Build.Mirpur).Error?.Code);
        Assert.Equal(ScanOutcome.Recorded, parcel.DispatchTo(Build.Mirpur, Build.Sylhet).Value);
        Assert.Equal(ScanOutcome.AlreadyRecorded, parcel.DispatchTo(Build.Mirpur, Build.Sylhet).Value);
        Assert.Equal(ParcelStatus.InTransit, parcel.Status);
        Assert.Null(parcel.CurrentHubId);
        Assert.Equal(Build.Sylhet, parcel.TransferToHubId);

        parcel.ReceiveAt(Build.Sylhet, Today);

        Assert.Equal(ParcelStatus.AtHub, parcel.Status);
        Assert.Equal(Build.Sylhet, parcel.CurrentHubId);
        Assert.Null(parcel.TransferToHubId);
    }

    [Fact]
    public void A_parcel_delivered_from_this_hub_is_not_sent_away_and_one_not_here_is_not_dispatched()
    {
        var parcel = Build.Parcel();

        Assert.Equal("parcel.dispatch.notHere", parcel.DispatchTo(Build.Mirpur, Build.Gulshan).Error?.Code);
        parcel.ReceiveAt(Build.Mirpur, Today);
        Assert.Equal("parcel.dispatch.deliverHere", parcel.DispatchTo(Build.Mirpur, Build.Gulshan).Error?.Code);
        Assert.Equal("parcel.dispatch.sameHub", Build.Parcel(deliveryHub: Build.Gulshan).Let(p => p.ReceiveAt(Build.Mirpur, Today)).DispatchTo(Build.Mirpur, Build.Mirpur).Error?.Code);
    }

    [Fact]
    public void A_parcel_at_another_hub_or_with_a_rider_is_not_received_here()
    {
        var atGulshan = Build.Parcel(deliveryHub: Build.Gulshan);
        atGulshan.ReceiveAt(Build.Gulshan, Today);
        Assert.Equal("parcel.scan.otherHub", atGulshan.ReceiveAt(Build.Mirpur, Today).Error?.Code);

        Assert.Equal("parcel.scan.withRider", Build.OutForDelivery().ReceiveAt(Build.Mirpur, Today).Error?.Code);
    }

    [Fact]
    public void A_rider_takes_a_parcel_waiting_at_its_delivering_hub_and_it_is_with_them_only()
    {
        var parcel = Build.Parcel();
        parcel.ReceiveAt(Build.Mirpur, Today);

        Assert.True(parcel.AssignTo(5, Build.Mirpur).IsSuccess);

        Assert.Equal(ParcelStatus.OutForDelivery, parcel.Status);
        Assert.Equal(5, parcel.RiderId);
        Assert.Null(parcel.CurrentHubId);
        Assert.Equal("parcel.assign.notHere", parcel.AssignTo(6, Build.Mirpur).Error?.Code);
    }

    [Fact]
    public void Delivered_with_the_whole_cash_closes_the_parcel_with_its_cod_charge()
    {
        var parcel = Build.OutForDelivery(cod: 1250);

        Assert.True(parcel.Deliver(1250, null, Now).IsSuccess);

        Assert.Equal(ParcelStatus.Delivered, parcel.Status);
        Assert.Equal(1250, parcel.CollectedAmount);
        Assert.Equal(13, parcel.CodCharge);
        Assert.Equal(60 + 13, parcel.TotalCharge);
        Assert.Equal(1, parcel.Attempts);
        Assert.Null(parcel.RiderId);
        Assert.Equal(Now, parcel.ClosedOn);
        Assert.True(parcel.IsFinal);
    }

    [Fact]
    public void Less_cash_is_a_partial_delivery_and_needs_a_reason()
    {
        var parcel = Build.OutForDelivery(cod: 1000);

        Assert.Equal("parcel.deliver.reason", parcel.Deliver(600, " ", Now).Error?.Code);
        Assert.Equal("parcel.deliver.amount", parcel.Deliver(1001, null, Now).Error?.Code);
        Assert.True(parcel.Deliver(600, "Kept one of two", Now).IsSuccess);

        Assert.Equal(ParcelStatus.PartlyDelivered, parcel.Status);
        Assert.Equal(6, parcel.CodCharge);
    }

    [Fact]
    public void A_held_parcel_goes_back_to_the_hub_and_out_again_until_the_last_attempt()
    {
        var parcel = Build.OutForDelivery();
        var until = new DateOnly(2026, 10, 6);

        Assert.True(parcel.Hold("Customer not reachable", until, Today, maxAttempts: 3).IsSuccess);
        Assert.Equal(ParcelStatus.OnHold, parcel.Status);
        Assert.Equal(until, parcel.HoldUntil);
        Assert.Equal(5, parcel.RiderId);

        parcel.ReceiveAt(Build.Mirpur, Today);
        Assert.Equal(Build.Mirpur, parcel.CurrentHubId);
        Assert.Null(parcel.RiderId);
        Assert.Equal(ParcelStatus.OnHold, parcel.Status);

        parcel.AssignTo(5, Build.Mirpur);
        Assert.True(parcel.Hold("Asked for another day", null, Today, 3).IsSuccess);
        parcel.ReceiveAt(Build.Mirpur, Today);
        parcel.AssignTo(5, Build.Mirpur);

        Assert.Equal("parcel.hold.attempts", parcel.Hold("Again", null, Today, 3).Error?.Code);
        Assert.Equal(2, parcel.Attempts);
    }

    [Fact]
    public void A_hold_for_a_day_already_come_is_refused()
    {
        var parcel = Build.OutForDelivery();

        Assert.Equal("parcel.hold.until", parcel.Hold("Asked for another day", Today, Today, 3).Error?.Code);
        Assert.Equal("parcel.hold.until", parcel.Hold("Asked for another day", Today.AddDays(-1), Today, 3).Error?.Code);
        Assert.Equal(ParcelStatus.OutForDelivery, parcel.Status);
        Assert.Equal(0, parcel.Attempts);
    }

    [Fact]
    public void A_refused_parcel_comes_back_through_the_hubs_and_is_handed_back_where_it_was_collected()
    {
        var parcel = Build.Parcel(pickupHub: Build.Mirpur, deliveryHub: Build.Gulshan);
        parcel.PickUp(Today);
        parcel.ReceiveAt(Build.Mirpur, Today);
        parcel.DispatchTo(Build.Mirpur, Build.Gulshan);
        parcel.ReceiveAt(Build.Gulshan, Today);
        parcel.AssignTo(5, Build.Gulshan);

        Assert.Equal("parcel.refuse.reason", parcel.Refuse(null).Error?.Code);
        Assert.True(parcel.Refuse("Did not order it").IsSuccess);
        Assert.Equal(ParcelStatus.Returning, parcel.Status);

        parcel.ReceiveAt(Build.Gulshan, Today);
        Assert.Equal(parcel.DeliveryCharge + parcel.ReturnCharge, parcel.TotalCharge);
        Assert.Equal("parcel.handBack.otherHub", parcel.ReturnToMerchant(Build.Gulshan, Now).Error?.Code);
        Assert.Equal(ScanOutcome.Recorded, parcel.DispatchTo(Build.Gulshan, Build.Mirpur).Value);
        Assert.Equal(ParcelStatus.Returning, parcel.Status);
        parcel.ReceiveAt(Build.Mirpur, Today);
        Assert.Equal("parcel.dispatch.returnHere", parcel.DispatchTo(Build.Mirpur, Build.Gulshan).Error?.Code);

        Assert.Equal(ScanOutcome.Recorded, parcel.ReturnToMerchant(Build.Mirpur, Now).Value);
        Assert.Equal(ScanOutcome.AlreadyRecorded, parcel.ReturnToMerchant(Build.Mirpur, Now).Value);
        Assert.Equal(ParcelStatus.Returned, parcel.Status);
        Assert.Null(parcel.CurrentHubId);
        Assert.Equal(60 + 0, parcel.TotalCharge);
    }

    [Fact]
    public void A_merchant_cancels_or_edits_only_before_pickup()
    {
        var parcel = Build.Parcel();
        var edited = parcel.Edit(new ParcelDetails(1, Build.Mirpur, "Karim", PhoneNumber.Parse("01911000000").Value, "House 2", 900, 1500, null, "Call first", Build.Rate(Pricing.ServiceArea.InsideCity).ChargesFor(1500)));
        Assert.True(edited.IsSuccess);
        Assert.Equal(75, parcel.DeliveryCharge);
        Assert.Equal("Karim", parcel.RecipientName);

        parcel.PickUp(Today);

        Assert.Equal("parcel.cancel", parcel.Cancel(null, Now).Error?.Code);
        Assert.Equal("parcel.edit", parcel.Edit(new ParcelDetails(1, 1, "X", PhoneNumber.Parse("01911000000").Value, "Y", 0, 500, null, null, parcel.Charges)).Error?.Code);

        var other = Build.Parcel(id: 101);
        Assert.True(other.Cancel("Customer changed their mind", Now).IsSuccess);
        Assert.Equal(ParcelStatus.Cancelled, other.Status);
        Assert.Equal(0, other.TotalCharge);
    }

    [Fact]
    public void A_return_is_asked_for_a_parcel_on_its_way_but_not_one_still_at_the_merchant()
    {
        var pending = Build.Parcel();
        Assert.Equal("parcel.return", pending.RequestReturn("No").Error?.Code);

        var atHub = Build.Parcel(id: 102);
        atHub.ReceiveAt(Build.Mirpur, Today);
        Assert.True(atHub.RequestReturn(null).IsSuccess);
        Assert.Equal(ParcelStatus.Returning, atHub.Status);
        Assert.Equal("Return requested", atHub.ReturnReason);
        Assert.Equal("parcel.notOut", atHub.Deliver(0, null, Now).Error?.Code);
    }

    [Theory]
    [InlineData(Pricing.ServiceArea.InsideCity, 1)]
    [InlineData(Pricing.ServiceArea.Suburb, 2)]
    [InlineData(Pricing.ServiceArea.OutsideCity, 3)]
    public void The_days_to_deliver_in_start_when_the_courier_takes_the_parcel(Pricing.ServiceArea area, int days)
    {
        var parcel = Build.Parcel(area: area);
        Assert.Equal(days, parcel.DeliveryDays);
        Assert.Null(parcel.DueOn);

        parcel.PickUp(Today);
        parcel.ReceiveAt(Build.Mirpur, Today.AddDays(1));

        Assert.Equal(Today.AddDays(days), parcel.DueOn);
    }

    [Fact]
    public void A_parcel_dropped_at_the_hub_is_due_from_the_drop_off_and_one_with_no_promise_is_never_due()
    {
        var dropped = Build.Parcel();
        dropped.ReceiveAt(Build.Mirpur, Today);
        Assert.Equal(Today.AddDays(1), dropped.DueOn);

        var unpromised = Parcel.Create(new NewParcel(
            7,
            1,
            Build.Mirpur,
            new ParcelDetails(1, Build.Mirpur, "Rahim", PhoneNumber.Parse("01811000101").Value, "House 1", 0, 500, null, null, Build.Rate(Pricing.ServiceArea.InsideCity, Build.InsideCity with { DeliveryDays = null }).ChargesFor(500)))).Value;
        unpromised.PickUp(Today);
        Assert.Null(unpromised.DueOn);
        Assert.False(unpromised.IsLate(Today.AddDays(30)));
    }

    [Fact]
    public void A_parcel_still_on_its_way_after_its_day_is_late_until_it_is_delivered()
    {
        var parcel = Build.OutForDelivery();

        Assert.False(parcel.IsLate(Today.AddDays(1)));
        Assert.True(parcel.IsLate(Today.AddDays(2)));
        Assert.True(parcel.Deliver(parcel.CodAmount, null, Now).IsSuccess);
        Assert.False(parcel.IsLate(Today.AddDays(2)));
    }

    [Fact]
    public void A_parcel_going_back_to_the_merchant_is_not_late()
    {
        var parcel = Build.OutForDelivery();
        Assert.True(parcel.Refuse("Ordered by mistake").IsSuccess);

        Assert.False(parcel.IsLate(Today.AddDays(5)));
    }

    [Fact]
    public void A_later_day_the_customer_asks_for_moves_the_day_the_parcel_is_due()
    {
        var asked = Build.OutForDelivery();
        Assert.True(asked.Hold("Customer travelling", Today.AddDays(4), Today, maxAttempts: 3).IsSuccess);
        Assert.Equal(Today.AddDays(4), asked.DueOn);
        Assert.False(asked.IsLate(Today.AddDays(3)));

        var unreachable = Build.OutForDelivery();
        Assert.True(unreachable.Hold("Not reachable", null, Today, maxAttempts: 3).IsSuccess);
        Assert.Equal(Today.AddDays(1), unreachable.DueOn);
        Assert.True(unreachable.IsLate(Today.AddDays(2)));
    }

    [Fact]
    public void A_hub_weighing_a_parcel_charges_the_merchant_for_what_the_scale_says()
    {
        // Booked as 500 g inside the city: the first kilogram costs 60, each started kilogram above it 15
        var parcel = Build.Parcel(grams: 500);
        parcel.PickUp(Today);
        parcel.ReceiveAt(Build.Mirpur, Today);
        Assert.Equal(60, parcel.DeliveryCharge);

        Assert.True(parcel.Reweigh(4800, Build.Mirpur).IsSuccess);

        Assert.Equal(4800, parcel.MeasuredWeightGrams);
        Assert.Equal(500, parcel.WeightGrams);
        Assert.Equal(4800, parcel.BilledWeightGrams);
        Assert.Equal(60 + 4 * 15, parcel.DeliveryCharge);
        Assert.Contains("0.5 kg booked, 4.8 kg measured", parcel.Events[^1].Note);
    }

    [Fact]
    public void Weighing_a_parcel_again_prices_it_at_the_rates_it_was_booked_under_not_todays()
    {
        var rate = Build.Rate(ServiceArea.InsideCity);
        var parcel = Build.Parcel(grams: 500);
        parcel.PickUp(Today);
        parcel.ReceiveAt(Build.Mirpur, Today);

        // The courier doubles its rate card after the parcel was booked
        Assert.True(rate.Change(new RateValues(1000, 120, 30, 1, 0, 1)).IsSuccess);
        Assert.True(parcel.Reweigh(4800, Build.Mirpur).IsSuccess);

        Assert.Equal(60 + 4 * 15, parcel.DeliveryCharge);
    }

    [Fact]
    public void A_parcel_is_weighed_only_while_the_courier_still_has_it_to_deliver()
    {
        var booked = Build.Parcel(grams: 500);
        Assert.Equal("parcel.reweigh", booked.Reweigh(4800, Build.Mirpur).Error?.Code);

        var delivered = Build.OutForDelivery(cod: 1250);
        Assert.True(delivered.Deliver(1250, null, Now).IsSuccess);
        Assert.Equal("parcel.reweigh", delivered.Reweigh(4800, Build.Mirpur).Error?.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30_001)]
    public void A_weight_the_scale_could_not_have_read_is_refused(int grams)
    {
        var parcel = Build.OutForDelivery();

        Assert.Equal("parcel.reweigh.weight", parcel.Reweigh(grams, Build.Mirpur).Error?.Code);
    }

    [Fact]
    public void A_flagged_parcel_keeps_its_status_and_stays_off_a_rider_until_the_flag_is_cleared()
    {
        var parcel = Build.Parcel();
        parcel.PickUp(Today);
        parcel.ReceiveAt(Build.Mirpur, Today);
        parcel.ClearDomainEvents();

        Assert.True(parcel.Flag(ParcelIssue.InReview, "Address unclear", Build.Mirpur, Now).IsSuccess);

        Assert.Equal((ParcelIssue.InReview, "Address unclear", Now), (parcel.Issue, parcel.IssueNote, parcel.IssueRaisedOn));
        Assert.Equal(ParcelStatus.AtHub, parcel.Status);
        Assert.Equal("In review: Address unclear", parcel.Events[^1].Note);
        Assert.Empty(parcel.GetDomainEvents());
        Assert.Equal("parcel.assign.flagged", parcel.AssignTo(5, Build.Mirpur).Error?.Code);

        Assert.True(parcel.ClearFlag("Called the customer", Build.Mirpur).IsSuccess);

        Assert.Equal((null, null, null), (parcel.Issue, parcel.IssueNote, parcel.IssueRaisedOn));
        Assert.Equal("Review finished: Called the customer", parcel.Events[^1].Note);
        Assert.True(parcel.AssignTo(5, Build.Mirpur).IsSuccess);
    }

    [Fact]
    public void Flagging_again_changes_the_kind_and_keeps_the_time_it_was_first_flagged()
    {
        var parcel = Build.Parcel();
        parcel.PickUp(Today);
        parcel.Flag(ParcelIssue.InReview, "Box looks wet", null, Now);

        Assert.True(parcel.Flag(ParcelIssue.Exceptional, "Box crushed, items broken", null, Now.AddHours(2)).IsSuccess);
        Assert.Equal((ParcelIssue.Exceptional, "Box crushed, items broken", Now), (parcel.Issue, parcel.IssueNote, parcel.IssueRaisedOn));

        parcel.ClearFlag(null, null);
        Assert.Equal("Exception settled", parcel.Events[^1].Note);
    }

    [Fact]
    public void Only_a_parcel_the_courier_has_is_flagged_and_only_with_what_is_wrong()
    {
        var waiting = Build.Parcel();
        var delivered = Build.OutForDelivery().Let(p => p.Deliver(1250, null, Now));
        var moving = Build.OutForDelivery();

        Assert.Equal("parcel.flag", waiting.Flag(ParcelIssue.InReview, "Check it", null, Now).Error?.Code);
        Assert.Equal("parcel.flag", delivered.Flag(ParcelIssue.Exceptional, "Damaged", null, Now).Error?.Code);
        Assert.Equal("parcel.flag.note", moving.Flag(ParcelIssue.InReview, " ", null, Now).Error?.Code);
        Assert.Equal("parcel.flag.note", moving.Flag(ParcelIssue.InReview, new string('x', 201), null, Now).Error?.Code);
        Assert.Equal("parcel.flag.kind", moving.Flag((ParcelIssue)9, "Odd", null, Now).Error?.Code);
        Assert.Equal("parcel.flag.none", moving.ClearFlag(null, null).Error?.Code);
    }

    [Fact]
    public void A_parcel_flagged_while_with_a_rider_is_still_recorded_at_the_door_and_keeps_its_flag()
    {
        var parcel = Build.OutForDelivery();

        Assert.True(parcel.Flag(ParcelIssue.Exceptional, "Rider says the box was opened", null, Now).IsSuccess);
        Assert.True(parcel.Deliver(1250, null, Now).IsSuccess);

        Assert.Equal((ParcelStatus.Delivered, ParcelIssue.Exceptional), (parcel.Status, parcel.Issue));
        Assert.True(parcel.ClearFlag("Merchant agreed it was sealed", null).IsSuccess);
    }
}

internal static class SetUp
{
    /// <summary>Does something to a value and hands it on, for one-line set-ups.</summary>
    public static T Let<T>(this T value, Action<T> action)
    {
        action(value);

        return value;
    }
}
