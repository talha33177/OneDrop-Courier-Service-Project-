using Domain.Common;
using Domain.Parcels;

namespace Domain.Delivery;

/// <summary>Stored as TINYINT. Never renumber a value that has been saved.</summary>
public enum ReturnListStatus : byte
{
    /// <summary>A rider of the hub is taking the parcels to the merchant.</summary>
    Out = 1,

    /// <summary>The rider handed the parcels over; the merchant has still to confirm it received them.</summary>
    HandedOver = 2,

    /// <summary>The merchant confirmed it received the parcels. Final.</summary>
    Confirmed = 3,

    /// <summary>The rider could not hand the parcels over and brings them back to the hub. Final.</summary>
    NotHandedOver = 4
}

/// <summary>
/// Returning parcels the hub that collected them sends back to one of a merchant's pickup points with a rider, instead
/// of handing them over at its counter. The rider records the hand-over (each parcel is then returned and charged) or
/// that it could not be made (the parcels come back to the hub with the rider and are scanned in again). The merchant
/// then confirms in its panel that it received them, its signature for the returns, with a note when something is
/// missing. <see cref="Number"/> (RL-100001) comes from a database sequence. Carries <see cref="MerchantId"/>, so a
/// merchant sees only its own.
/// </summary>
public class ReturnList : TenantEntity, IMerchantOwned
{
    public const int MaxNoteLength = 300;

    private readonly List<ReturnListParcel> parcels = [];

    private ReturnList()
    {
    }

    public long MerchantId { get; private set; }

    /// <summary>Where the rider takes the parcels: the pickup point they were collected from.</summary>
    public long PickupPointId { get; private set; }

    public long HubId { get; private set; }

    public long RiderId { get; private set; }

    /// <summary>RL-100001, assigned by the database on insert; null until saved, so the sequence fills it.</summary>
    public string Number { get; private set; } = null!;

    public ReturnListStatus Status { get; private set; }

    public DateTime? HandedOverOn { get; private set; }

    public DateTime? ConfirmedOn { get; private set; }

    /// <summary>The merchant's login that confirmed the parcels arrived.</summary>
    public long? ConfirmedById { get; private set; }

    /// <summary>Why the rider could not hand the parcels over, or what the merchant said when confirming.</summary>
    public string? Note { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyList<ReturnListParcel> Parcels => parcels;

    /// <summary>
    /// The hub <paramref name="hubId"/> sends <paramref name="returning"/> back with <paramref name="rider"/>, one of its
    /// active riders. All of them belong to one merchant's pickup point and can go back from this hub; none is sent
    /// unless all can be.
    /// </summary>
    public static Result<ReturnList> Send(Rider rider, long hubId, IReadOnlyList<Parcel> returning)
    {
        if (rider.IsNew)
        {
            throw new InvalidOperationException("Save the rider before sending parcels with them.");
        }

        if (rider.HubId != hubId || rider.Archived)
        {
            return Error.Validation("returnList.rider", "Choose an active rider of this hub.");
        }

        if (returning.Count == 0)
        {
            return Error.Validation("returnList.none", "Choose the parcels that go back to the merchant.");
        }

        var first = returning[0];
        if (returning.Any(p => p.MerchantId != first.MerchantId || p.PickupPointId != first.PickupPointId))
        {
            return Error.Validation("returnList.mixed", "A return list goes to one pickup point of one merchant. Make a list for each.");
        }

        if (returning.Select(p => p.CanGoBackFrom(hubId)).FirstOrDefault(check => check.IsFailure) is { } refused)
        {
            return refused.Error!;
        }

        var list = new ReturnList
        {
            MerchantId = first.MerchantId,
            PickupPointId = first.PickupPointId,
            HubId = hubId,
            RiderId = rider.Id,
            Status = ReturnListStatus.Out
        };
        foreach (var parcel in returning)
        {
            parcel.SendBack(rider.Id, hubId);
            list.parcels.Add(new ReturnListParcel(list, parcel));
        }

        return list;
    }

    /// <summary>The rider handed the parcels to the merchant: each is returned. <paramref name="onList"/> is every parcel of the list.</summary>
    public Result HandOver(IReadOnlyList<Parcel> onList, DateTime now)
    {
        var ready = Ready(onList);
        if (ready.IsFailure)
        {
            return ready;
        }

        foreach (var parcel in onList)
        {
            var handed = parcel.HandBackAtDoor(RiderId, now);
            if (handed.IsFailure)
            {
                return handed;
            }
        }

        Status = ReturnListStatus.HandedOver;
        HandedOverOn = now;
        Raise(new ReturnListHandedOver(this));

        return Result.Success();
    }

    /// <summary>The rider could not hand the parcels over, and says why; they bring them back to the hub.</summary>
    public Result Miss(IReadOnlyList<Parcel> onList, string? reason)
    {
        var why = reason.NullIfBlank();
        if (why is null || why.Length > 200)
        {
            return Error.Validation("returnList.reason", "Say why the parcels could not be handed over, at most 200 characters.");
        }

        var ready = Ready(onList);
        if (ready.IsFailure)
        {
            return ready;
        }

        foreach (var parcel in onList)
        {
            var missed = parcel.MissHandBack(RiderId, why);
            if (missed.IsFailure)
            {
                return missed;
            }
        }

        Status = ReturnListStatus.NotHandedOver;
        Note = why;

        return Result.Success();
    }

    /// <summary>
    /// The merchant confirms it received the parcels the rider handed over, by the login <paramref name="userId"/>; a
    /// note says what was missing or damaged. Once.
    /// </summary>
    public Result Confirm(long? userId, string? note, DateTime now)
    {
        if (Status == ReturnListStatus.Confirmed)
        {
            return Error.Conflict("returnList.confirmed", $"Return list {Number} is already confirmed.");
        }

        if (Status != ReturnListStatus.HandedOver)
        {
            return Error.Conflict(
                "returnList.notHandedOver",
                $"Return list {Number} has not been handed over to you, so there is nothing to confirm.");
        }

        var said = note.NullIfBlank();
        if (said?.Length > MaxNoteLength)
        {
            return Error.Validation("returnList.note", $"The note is at most {MaxNoteLength} characters.");
        }

        Status = ReturnListStatus.Confirmed;
        ConfirmedOn = now;
        ConfirmedById = userId;
        Note = said;

        return Result.Success();
    }

    private Result Ready(IReadOnlyList<Parcel> onList)
    {
        if (Status != ReturnListStatus.Out)
        {
            return Error.Conflict("returnList.done", $"Return list {Number} is no longer out with the rider.");
        }

        if (!onList.Select(parcel => parcel.Id).Order().SequenceEqual(parcels.Select(line => line.ParcelId).Order()))
        {
            throw new InvalidOperationException("Pass every parcel of the return list, and only those.");
        }

        return Result.Success();
    }
}

/// <summary>One parcel on a return list. Carries <see cref="MerchantId"/> so the merchant filter needs no join.</summary>
public class ReturnListParcel : TenantEntity, IMerchantOwned
{
    private ReturnListParcel()
    {
    }

    internal ReturnListParcel(ReturnList list, Parcel parcel)
    {
        ReturnList = list;
        ParcelId = parcel.Id;
        MerchantId = parcel.MerchantId;
    }

    public long ReturnListId { get; private set; }

    public ReturnList? ReturnList { get; private set; }

    public long ParcelId { get; private set; }

    public long MerchantId { get; private set; }
}

/// <summary>A rider handed a return list to the merchant: the merchant is asked by email to confirm it.</summary>
public sealed record ReturnListHandedOver(ReturnList List) : IDomainEvent;
