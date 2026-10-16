using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Domain.Common;
using Domain.Delivery;

namespace Application.Hubs.Runs;

/// <summary>A rider's run as the hub's closing page shows it.</summary>
public sealed record RunRow(
    long Id,
    string Rider,
    string RiderPhone,
    DateOnly Date,
    RunStatus Status,
    int Assigned,
    int Delivered,
    int Held,
    int Refused,
    int WithoutOutcome,
    decimal CashCollected,
    decimal? CashReceived,
    DateTime? ClosedOn)
{
    public decimal? Short => CashReceived is null ? null : CashCollected - CashReceived;

    /// <summary>When the rider is due back with the cash, on the courier's clock; null when it sets no time.</summary>
    public DateTime? DueBack { get; init; }

    /// <summary>Still open after the rider was due back.</summary>
    public bool Late { get; init; }
}

/// <summary>One parcel on the paper run sheet a rider carries, in the order the hub hands them over.</summary>
public sealed record RunSheetStop(
    string TrackingCode,
    string Recipient,
    string Phone,
    string Address,
    string Area,
    decimal CodAmount,
    string? Note,
    int Attempt,
    int OfAttempts);

/// <summary>A rider's run on paper: who they are, the day, every stop and the cash they should bring back.</summary>
public sealed record RunSheet(
    long RunId,
    string Rider,
    string RiderPhone,
    string Vehicle,
    string Hub,
    DateOnly Date,
    IReadOnlyList<RunSheetStop> Stops)
{
    public decimal CashDue => Stops.Sum(stop => stop.CodAmount);
}

/// <summary>
/// The end of a rider's day at the hub: their run sheets still open (today's and any earlier ones) and today's closed
/// ones. Closing counts the cash the deliveries collected against what the rider handed in and takes back the parcels
/// they could not deliver, which are then at the hub again.
/// </summary>
public class RunsHandler(IAppDbContext db, ITenantContext tenantContext, HubDirectory hubs, TimeProvider time)
{
    public static readonly Error NotFound = Error.NotFound("run.notFound", "That run was not found at this hub.");

    public async Task<IReadOnlyList<RunRow>?> ListAsync(string hubCode, CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var hub = await hubs.FindAsync(hubCode, cancellationToken);
        if (hub is null)
        {
            return null;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var today = tenant.Today(now);
        var rows = await (
            from run in db.DeliveryRuns
            join rider in db.Riders on run.RiderId equals rider.Id
            where run.HubId == hub.Id && (run.Status == RunStatus.Open || run.RunDate == today)
            orderby run.Status, run.RunDate, rider.Name
            select new RunRow(
                run.Id,
                rider.Name,
                rider.Phone,
                run.RunDate,
                run.Status,
                db.DeliveryAttempts.Count(a => a.RunId == run.Id),
                db.DeliveryAttempts.Count(a => a.RunId == run.Id &&
                    (a.Outcome == AttemptOutcome.Delivered || a.Outcome == AttemptOutcome.PartlyDelivered)),
                db.DeliveryAttempts.Count(a => a.RunId == run.Id && a.Outcome == AttemptOutcome.Hold),
                db.DeliveryAttempts.Count(a => a.RunId == run.Id && a.Outcome == AttemptOutcome.Refused),
                db.DeliveryAttempts.Count(a => a.RunId == run.Id && a.Outcome == null),
                db.DeliveryAttempts.Where(a => a.RunId == run.Id).Sum(a => (decimal?)a.CollectedAmount) ?? 0,
                run.CashReceived,
                run.ClosedOn))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(row => row with
            {
                ClosedOn = row.ClosedOn is { } closed ? tenant.Local(closed) : null,
                DueBack = tenant.DueBack(row.Date),
                Late = row.Status == RunStatus.Open && tenant.RunIsLate(row.Date, now)
            })
        ];
    }

    /// <summary>
    /// The run as a sheet of paper the rider carries: every parcel still to hand over, with the door, the phone and
    /// the cash to collect. Parcels already recorded are left off, so a sheet printed again later is the work left.
    /// Null when the run is not this hub's.
    /// </summary>
    public async Task<RunSheet?> SheetAsync(string hubCode, long runId, CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var hub = await hubs.FindAsync(hubCode, cancellationToken);
        if (hub is null)
        {
            return null;
        }

        var run = await (
            from r in db.DeliveryRuns
            join rider in db.Riders on r.RiderId equals rider.Id
            where r.Id == runId && r.HubId == hub.Id
            select new { r.Id, r.RunDate, Rider = rider.Name, rider.Phone, rider.Vehicle })
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (run is null)
        {
            return null;
        }

        var stops = await (
            from attempt in db.DeliveryAttempts
            join parcel in db.Parcels on attempt.ParcelId equals parcel.Id
            join area in db.Areas on parcel.AreaId equals area.Id
            where attempt.RunId == run.Id && attempt.Outcome == null
            orderby area.Name, parcel.TrackingCode
            select new RunSheetStop(
                parcel.TrackingCode,
                parcel.RecipientName,
                parcel.RecipientPhone,
                parcel.RecipientAddress,
                area.Name,
                parcel.CodAmount - (parcel.PaidOnline ?? 0),
                parcel.Note,
                parcel.Attempts + 1,
                tenant.MaxDeliveryAttempts))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return new RunSheet(
            run.Id,
            run.Rider,
            run.Phone,
            run.Vehicle.ToString(),
            hub.Name,
            run.RunDate,
            stops);
    }

    /// <summary>
    /// The rider handed in <paramref name="received"/> in cash. Every parcel of the run needs an outcome first; the ones
    /// held or refused come back to the hub with the closing.
    /// </summary>
    public async Task<Result> CloseAsync(string hubCode, long runId, decimal received, CancellationToken cancellationToken = default)
    {
        var hub = await hubs.FindAsync(hubCode, cancellationToken);
        var run = hub is null
            ? null
            : await db.DeliveryRuns.SingleOrDefaultAsync(r => r.Id == runId && r.HubId == hub.Id, cancellationToken);
        if (run is null)
        {
            return NotFound;
        }

        var attempts = await db.DeliveryAttempts.Where(a => a.RunId == run.Id).ToListAsync(cancellationToken);
        var open = attempts.Count(a => !a.IsDone);
        if (open > 0)
        {
            return Error.Conflict(
                "run.open",
                $"The rider has not recorded what happened to {open} parcel{(open == 1 ? "" : "s")} yet. They record it on their phone first.");
        }

        var closed = run.Close(attempts.Sum(a => a.CollectedAmount), received, time.GetUtcNow().UtcDateTime);
        if (closed.IsFailure)
        {
            return closed;
        }

        // The parcels the rider brings back: held for another day or refused, still with this rider
        var backIds = attempts
            .Where(a => a.Outcome is AttemptOutcome.Hold or AttemptOutcome.Refused)
            .Select(a => a.ParcelId)
            .ToList();
        var back = await db.Parcels
            .Where(p => backIds.Contains(p.Id) && p.RiderId == run.RiderId)
            .ToListAsync(cancellationToken);
        foreach (var parcel in back)
        {
            parcel.ReceiveAt(run.HubId, run.RunDate);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("run.changed", "This run changed just now. Reload and close it again.");
        }

        return Result.Success();
    }
}
