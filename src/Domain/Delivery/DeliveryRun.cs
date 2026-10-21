using Domain.Common;

namespace Domain.Delivery;

/// <summary>Stored as TINYINT. Never renumber a value that has been saved.</summary>
public enum RunStatus : byte
{
    /// <summary>The rider is out, or has parcels and cash still to bring back.</summary>
    Open = 1,

    /// <summary>The hub received the rider's cash and the parcels not delivered.</summary>
    Closed = 2
}

/// <summary>
/// A rider's run sheet for one day (the tenant's date): every parcel the hub gave them to deliver that day is a
/// <see cref="DeliveryAttempt"/> on it. At the end of the day the hub closes it: it counts the cash the rider collected
/// against what the attempts say, and takes back the parcels not delivered. One run per rider a day
/// (<c>UX_DeliveryRun_Rider_RunDate</c>).
/// </summary>
public class DeliveryRun : TenantEntity
{
    private DeliveryRun()
    {
    }

    public long RiderId { get; private set; }

    /// <summary>The hub the run leaves from and the cash is handed in at.</summary>
    public long HubId { get; private set; }

    public DateOnly RunDate { get; private set; }

    public RunStatus Status { get; private set; }

    /// <summary>The cash the attempts say the rider collected, as counted when the run was closed.</summary>
    public decimal? CashExpected { get; private set; }

    /// <summary>The cash hub staff received from the rider.</summary>
    public decimal? CashReceived { get; private set; }

    public DateTime? ClosedOn { get; private set; }

    /// <summary>What the rider handed in less than they collected; negative when they handed in more.</summary>
    public decimal? CashShort => CashExpected - CashReceived;

    /// <summary>When the hub manager accepted the rider's shortfall; null while a shortfall waits for them.</summary>
    public DateTime? ShortAcceptedOn { get; private set; }

    /// <summary>The hub manager's login that accepted the shortfall.</summary>
    public long? ShortAcceptedById { get; private set; }

    /// <summary>What the hub manager said was done about the shortfall.</summary>
    public string? ShortNote { get; private set; }

    /// <summary>The run closed with less cash than the rider collected and the hub manager has not settled it yet.</summary>
    public bool ShortWaiting => Status == RunStatus.Closed && CashShort > 0 && ShortAcceptedOn is null;

    public const int MaxShortNoteLength = 200;

    public byte[] RowVersion { get; private set; } = [];

    public static DeliveryRun Open(Rider rider, DateOnly runDate)
    {
        if (rider.IsNew)
        {
            throw new InvalidOperationException("Save the rider before opening a run for them.");
        }

        return new DeliveryRun
        {
            RiderId = rider.Id,
            HubId = rider.HubId,
            RunDate = runDate,
            Status = RunStatus.Open
        };
    }

    /// <summary>A parcel handed to the rider for this run.</summary>
    public Result<DeliveryAttempt> Add(Parcels.Parcel parcel, DateTime now)
    {
        if (IsNew)
        {
            throw new InvalidOperationException("Save the run before adding parcels to it.");
        }

        if (Status != RunStatus.Open)
        {
            return Error.Conflict("run.closed", "This rider's run for the day is closed. Assign the parcel tomorrow.");
        }

        return new DeliveryAttempt(this, parcel, now);
    }

    /// <summary>
    /// Hub staff counted the rider's cash: <paramref name="expected"/> is what the rider's deliveries collected,
    /// <paramref name="received"/> what was handed in. Once. A shortfall is recorded, not refused: the hub follows it up.
    /// </summary>
    public Result Close(decimal expected, decimal received, DateTime now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expected);
        if (received < 0)
        {
            return Error.Validation("run.cash", "Enter the cash received; it cannot be negative.");
        }

        if (Status == RunStatus.Closed)
        {
            return Error.Conflict("run.closed", "This run has already been closed.");
        }

        CashExpected = expected;
        CashReceived = received;
        ClosedOn = now;
        Status = RunStatus.Closed;

        return Result.Success();
    }

    /// <summary>
    /// The hub manager, by the login <paramref name="userId"/>, accepts that the rider handed in less than they collected,
    /// and says what was done about it (<paramref name="note"/>: taken from the rider's pay, found later). Once.
    /// </summary>
    public Result AcceptShort(string? note, long? userId, DateTime now)
    {
        if (!ShortWaiting)
        {
            return Error.Conflict(
                "run.short.none",
                ShortAcceptedOn is null ? "This run has no cash shortfall to accept." : "This shortfall has already been accepted.");
        }

        var said = note.NullIfBlank();
        if (said is null || said.Length > MaxShortNoteLength)
        {
            return Error.Validation("run.short.note", $"Say what was done about the shortfall, at most {MaxShortNoteLength} characters.");
        }

        ShortAcceptedOn = now;
        ShortAcceptedById = userId;
        ShortNote = said;

        return Result.Success();
    }
}
