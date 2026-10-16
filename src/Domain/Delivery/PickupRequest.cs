using Domain.Common;

namespace Domain.Delivery;

/// <summary>Stored as TINYINT. Never renumber a value that has been saved.</summary>
public enum PickupStatus : byte
{
    /// <summary>On the day's pickup list; no rider yet.</summary>
    Scheduled = 1,

    /// <summary>A rider is going to collect.</summary>
    Assigned = 2,

    /// <summary>The rider collected the parcels.</summary>
    Completed = 3,

    /// <summary>Called off: nothing was left to collect, or its parcels went with another pickup.</summary>
    Cancelled = 4
}

/// <summary>
/// The day's pickup at one of a merchant's pickup points. The courier visits a point once a day: every parcel booked
/// there before the pickup van reaches it goes with that day's pickup, so pickups are scheduled as parcels are booked
/// and never asked for. The hub of the point's zone assigns a rider, who picks the parcels up and completes it, which
/// is the day's visit: what is booked at the point after it goes with the next day's pickup. A pickup the day passed
/// without is carried to the next day.
/// </summary>
public class PickupRequest : TenantEntity, IMerchantOwned
{
    private PickupRequest()
    {
    }

    public long MerchantId { get; private set; }

    public long PickupPointId { get; private set; }

    /// <summary>The hub that collects: the pickup point's zone's hub.</summary>
    public long HubId { get; private set; }

    /// <summary>The day the parcels are collected (the tenant's date).</summary>
    public DateOnly PickupDate { get; private set; }

    public PickupStatus Status { get; private set; }

    public long? RiderId { get; private set; }

    /// <summary>Parcels the rider collected; set on completion.</summary>
    public int? PickedParcels { get; private set; }

    public DateTime? CompletedOn { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public bool IsOpen => Status is PickupStatus.Scheduled or PickupStatus.Assigned;

    /// <summary>Puts a pickup point on the pickup list of <paramref name="day"/>.</summary>
    public static PickupRequest Schedule(long merchantId, long pickupPointId, long hubId, DateOnly day)
    {
        return new PickupRequest
        {
            MerchantId = merchantId,
            PickupPointId = pickupPointId,
            HubId = hubId,
            PickupDate = day,
            Status = PickupStatus.Scheduled
        };
    }

    /// <summary>
    /// The hub sends <paramref name="rider"/>, or another rider instead of the one it sent. Only a pickup van collects,
    /// and it must carry <paramref name="load"/> on top of what it <paramref name="carries"/> already (see
    /// <see cref="Fleet.Collect"/>).
    /// </summary>
    public Result Assign(Rider rider, Fleet fleet, Load carries, Load load)
    {
        if (!IsOpen)
        {
            return Error.Conflict("pickup.closed", "This pickup is already done or called off.");
        }

        if (rider.HubId != HubId || rider.Archived)
        {
            return Error.Validation("pickup.rider", "Choose an active rider of the hub that collects from this point.");
        }

        var fits = fleet.Collect(rider.Vehicle, carries, load);
        if (fits.IsFailure)
        {
            return fits;
        }

        RiderId = rider.Id;
        Status = PickupStatus.Assigned;

        return Result.Success();
    }

    /// <summary>The rider collected <paramref name="picked"/> parcels. Once.</summary>
    public Result Complete(int picked, DateTime now)
    {
        if (Status != PickupStatus.Assigned)
        {
            return Error.Conflict("pickup.notAssigned", "Only a pickup assigned to a rider can be completed.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(picked);
        PickedParcels = picked;
        CompletedOn = now;
        Status = PickupStatus.Completed;

        return Result.Success();
    }

    /// <summary>
    /// The pickup's day passed without it being made: it goes on the list of <paramref name="day"/>, today or, when
    /// the van has been to the point today, tomorrow, and waits for the hub to choose a rider again.
    /// </summary>
    public Result CarryTo(DateOnly day)
    {
        if (!IsOpen || day <= PickupDate)
        {
            return Error.Conflict("pickup.carry", "Only an open pickup whose day has passed is carried to another day.");
        }

        PickupDate = day;
        RiderId = null;
        Status = PickupStatus.Scheduled;

        return Result.Success();
    }

    /// <summary>Nothing is left to collect, or the parcels go with another pickup. Only before the pickup is done.</summary>
    public Result Cancel()
    {
        if (!IsOpen)
        {
            return Error.Conflict("pickup.closed", "This pickup is already done or called off.");
        }

        Status = PickupStatus.Cancelled;

        return Result.Success();
    }
}
