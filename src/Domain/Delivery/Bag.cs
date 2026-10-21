using Domain.Common;
using Domain.Parcels;

namespace Domain.Delivery;

/// <summary>Stored as TINYINT. Never renumber a value that has been saved.</summary>
public enum BagStatus : byte
{
    /// <summary>At the hub it leaves from, being filled for one other hub.</summary>
    Open = 1,

    /// <summary>Sealed and loaded on the truck: its parcels are on their way.</summary>
    Sealed = 2,

    /// <summary>Opened at the hub it was for, which is scanning its parcels in.</summary>
    Arrived = 3,

    /// <summary>Every parcel was scanned in, or the hub closed it and the ones not found were flagged missing. Final.</summary>
    Closed = 4
}

/// <summary>
/// A bag of parcels one hub sends to another on the line-haul truck. Line-haul scans each parcel into the open bag for
/// the hub it goes to next and seals the bag when the truck is loaded, which sends every parcel in it. The hub it is
/// for scans the bag, then each parcel; the bag closes itself when the last one is in, or the hub closes it, and every
/// parcel it did not find is flagged exceptional with the bag, the hubs and the people. One open bag per pair of hubs
/// (<c>UX_Bag_From_To_Open</c>). <see cref="Number"/> (BG-100001) comes from a database sequence.
/// </summary>
public class Bag : TenantEntity
{
    public const int MaxVehicleLength = 100;

    private readonly List<BagParcel> parcels = [];

    private Bag()
    {
    }

    public long FromHubId { get; private set; }

    public long ToHubId { get; private set; }

    /// <summary>BG-100001, assigned by the database on insert; null until saved, so the sequence fills it.</summary>
    public string Number { get; private set; } = null!;

    public BagStatus Status { get; private set; }

    /// <summary>The truck it went on, as line-haul wrote it: its number, the driver.</summary>
    public string? Vehicle { get; private set; }

    public DateTime? SealedOn { get; private set; }

    /// <summary>The login that sealed the bag.</summary>
    public long? SealedById { get; private set; }

    public DateTime? ArrivedOn { get; private set; }

    public DateTime? ClosedOn { get; private set; }

    /// <summary>The login that closed the bag with parcels missing; null when the last parcel scanned in closed it.</summary>
    public long? ClosedById { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyList<BagParcel> Parcels => parcels;

    public static Bag Open(long fromHubId, long toHubId)
    {
        if (fromHubId == toHubId)
        {
            throw new ArgumentException("A bag goes to another hub.", nameof(toHubId));
        }

        return new Bag { FromHubId = fromHubId, ToHubId = toHubId, Status = BagStatus.Open };
    }

    /// <summary>Line-haul put the parcel in the bag: it is at the bag's hub and goes next to the hub the bag is for.</summary>
    public Result<ScanOutcome> Add(Parcel parcel)
    {
        if (parcels.Any(line => line.ParcelId == parcel.Id))
        {
            return ScanOutcome.AlreadyRecorded;
        }

        if (Status != BagStatus.Open)
        {
            return Error.Conflict("bag.sealed", $"Bag {Number} is sealed. Put the parcel in the next bag.");
        }

        if (parcel.OnwardHubId() != ToHubId)
        {
            return Error.Conflict("bag.otherHub", $"{parcel.TrackingCode} goes to another hub, not in bag {Number}.");
        }

        var can = parcel.CanDispatch(FromHubId, ToHubId);
        if (can.IsFailure)
        {
            return can.Error!;
        }

        parcels.Add(new BagParcel(this, parcel));

        return ScanOutcome.Recorded;
    }

    /// <summary>The parcel comes out of the bag before it is sealed: it stays at the hub.</summary>
    public Result TakeOut(Parcel parcel)
    {
        if (Status != BagStatus.Open)
        {
            return Error.Conflict("bag.sealed", $"Bag {Number} is sealed; nothing comes out of it until it arrives.");
        }

        if (parcels.SingleOrDefault(line => line.ParcelId == parcel.Id) is not { } line)
        {
            return Error.NotFound("bag.notInside", $"{parcel.TrackingCode} is not in bag {Number}.");
        }

        parcels.Remove(line);

        return Result.Success();
    }

    /// <summary>
    /// Line-haul sealed the bag and loaded it on <paramref name="vehicle"/>: each parcel in it, <paramref name="inside"/>,
    /// leaves the hub. None leaves unless all can: a parcel that changed since it went in (sent back to its merchant, say)
    /// is named, to be taken out first.
    /// </summary>
    public Result Seal(IReadOnlyList<Parcel> inside, string? vehicle, long? userId, DateTime now)
    {
        if (Status != BagStatus.Open)
        {
            return Error.Conflict("bag.sealed", $"Bag {Number} is already sealed.");
        }

        if (parcels.Count == 0)
        {
            return Error.Validation("bag.empty", $"Bag {Number} is empty. Scan parcels into it before sealing it.");
        }

        if (!inside.Select(parcel => parcel.Id).Order().SequenceEqual(parcels.Select(line => line.ParcelId).Order()))
        {
            throw new InvalidOperationException("Pass every parcel in the bag, and only those.");
        }

        var truck = vehicle.NullIfBlank();
        if (truck?.Length > MaxVehicleLength)
        {
            return Error.Validation("bag.vehicle", $"The truck is at most {MaxVehicleLength} characters.");
        }

        foreach (var parcel in inside)
        {
            if (parcel.OnwardHubId() != ToHubId || parcel.CanDispatch(FromHubId, ToHubId).IsFailure)
            {
                return Error.Conflict(
                    "bag.changed",
                    $"{parcel.TrackingCode} changed since it went in the bag and no longer goes with it. Take it out first.");
            }
        }

        foreach (var parcel in inside)
        {
            parcel.DispatchTo(FromHubId, ToHubId, Number);
        }

        Status = BagStatus.Sealed;
        Vehicle = truck;
        SealedOn = now;
        SealedById = userId;

        return Result.Success();
    }

    /// <summary>The hub <paramref name="hubId"/> scanned the bag off the truck to check what is inside.</summary>
    public Result<ScanOutcome> Arrive(long hubId, DateTime now)
    {
        if (hubId != ToHubId)
        {
            return Error.Conflict("bag.wrongHub", $"Bag {Number} is for another hub. Do not open it: send it on.");
        }

        switch (Status)
        {
            case BagStatus.Open:
                return Error.Conflict("bag.open", $"Bag {Number} has not been sealed and sent yet.");

            case BagStatus.Sealed:
                Status = BagStatus.Arrived;
                ArrivedOn = now;
                return ScanOutcome.Recorded;

            default:
                return ScanOutcome.AlreadyRecorded;
        }
    }

    /// <summary>
    /// A parcel of the bag was scanned in at <paramref name="hubId"/>. The bag counts as opened when that is its hub, and
    /// closes itself once every parcel is in. Whether this closed it.
    /// </summary>
    public bool Received(long parcelId, long hubId, DateTime now)
    {
        var line = parcels.SingleOrDefault(line => line.ParcelId == parcelId);
        if (line is null || line.ReceivedOn is not null || Status is not (BagStatus.Sealed or BagStatus.Arrived))
        {
            return false;
        }

        line.ReceivedOn = now;
        if (Status == BagStatus.Sealed && hubId == ToHubId)
        {
            Status = BagStatus.Arrived;
            ArrivedOn = now;
        }

        if (parcels.Any(p => p.ReceivedOn is null))
        {
            return false;
        }

        Status = BagStatus.Closed;
        ArrivedOn ??= now;
        ClosedOn = now;

        return true;
    }

    /// <summary>
    /// The hub the bag was for has scanned in what it found and closes it by the login <paramref name="userId"/>: each
    /// parcel not scanned in, <paramref name="missing"/>, is marked missing and flagged exceptional with
    /// <paramref name="who"/> (which hubs and people sealed and closed it), so the courier looks for it.
    /// </summary>
    public Result Close(long hubId, IReadOnlyList<Parcel> missing, string who, long? userId, DateTime now)
    {
        if (hubId != ToHubId)
        {
            return Error.Conflict("bag.wrongHub", $"Bag {Number} is for another hub; that hub closes it.");
        }

        if (Status is BagStatus.Open or BagStatus.Closed)
        {
            return Error.Conflict(
                "bag.notOnItsWay",
                Status == BagStatus.Open ? $"Bag {Number} has not been sent yet." : $"Bag {Number} is already closed.");
        }

        var notIn = parcels.Where(line => line.ReceivedOn is null).ToList();
        if (!missing.Select(parcel => parcel.Id).Order().SequenceEqual(notIn.Select(line => line.ParcelId).Order()))
        {
            throw new InvalidOperationException("Pass every parcel of the bag not scanned in, and only those.");
        }

        var note = $"Missing from bag {Number}: {who}";
        foreach (var parcel in missing)
        {
            var flagged = parcel.Flag(ParcelIssue.Exceptional, note.Length > 200 ? note[..200] : note, hubId, now);
            if (flagged.IsFailure)
            {
                return flagged;
            }
        }

        foreach (var line in notIn)
        {
            line.Missing = true;
        }

        Status = BagStatus.Closed;
        ArrivedOn ??= now;
        ClosedOn = now;
        ClosedById = userId;

        return Result.Success();
    }
}

/// <summary>One parcel in a bag: when the hub it was for scanned it in, or that it was missing when the bag was closed.</summary>
public class BagParcel : TenantEntity
{
    private BagParcel()
    {
    }

    internal BagParcel(Bag bag, Parcel parcel)
    {
        Bag = bag;
        ParcelId = parcel.Id;
    }

    public long BagId { get; private set; }

    public Bag? Bag { get; private set; }

    public long ParcelId { get; private set; }

    public DateTime? ReceivedOn { get; internal set; }

    public bool Missing { get; internal set; }
}
