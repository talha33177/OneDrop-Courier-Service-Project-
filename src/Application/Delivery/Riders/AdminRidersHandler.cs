using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Application.Notifications;
using Domain.Common;
using Domain.Delivery;

namespace Application.Delivery.Riders;

public sealed record RiderRow(
    long Id,
    string Name,
    string Phone,
    long HubId,
    string Hub,
    Vehicle Vehicle,
    string? Email,
    bool Active,
    int WithThem,
    int DeliveredToday,
    decimal CashInHand);

/// <summary>One of a rider's runs: its hand-overs, what came of them and the cash collected against handed in.</summary>
public sealed record RunLine(
    long Id,
    DateOnly Date,
    string Hub,
    RunStatus Status,
    int Parcels,
    int Delivered,
    int NotDelivered,
    decimal Collected,
    decimal? Expected,
    decimal? HandedIn)
{
    /// <summary>
    /// What the rider kept back of what the hub counted as due at closing; negative when they handed in more. Null while
    /// the run is open.
    /// </summary>
    public decimal? Short => Expected - HandedIn;
}

/// <summary>One hand-over of a parcel to the rider; <see cref="At"/> is when it was recorded, or given while still out.</summary>
public sealed record AttemptLine(string TrackingCode, DateTime At, AttemptOutcome? Outcome, decimal Collected, string? Reason);

public sealed record RiderDetails(RiderRow Rider, Period Period, IReadOnlyList<RunLine> Runs, IReadOnlyList<AttemptLine> Attempts)
{
    public int Stops => Runs.Sum(r => r.Delivered + r.NotDelivered);

    public int Delivered => Runs.Sum(r => r.Delivered);

    /// <summary>Delivered out of the doors the rider recorded; null before the first.</summary>
    public double? SuccessRate => Stops == 0 ? null : (double)Delivered / Stops;

    public decimal Collected => Runs.Sum(r => r.Collected);

    /// <summary>Handed in at the runs closed in the period.</summary>
    public decimal HandedIn => Runs.Sum(r => r.HandedIn ?? 0);

    /// <summary>Kept back over the closed runs; the open ones are still with the rider and counted as cash in hand.</summary>
    public decimal Short => Runs.Sum(r => r.Short ?? 0);

    public int OpenRuns => Runs.Count(r => r.Status == RunStatus.Open);
}

public sealed record NewRider(string? Name, string? Phone, long HubId, Vehicle Vehicle, string? Email, string? Password);

/// <summary>
/// The courier's riders: list them by hub with today's work, add one with a login, change their hub or details, or stop
/// and restart them. Courier admins only.
/// </summary>
public class AdminRidersHandler(IAppDbContext db, ITenantContext tenantContext, IUserAccounts accounts, TimeProvider time)
{
    public static readonly Error NotFound = Error.NotFound("rider.notFound", "That rider was not found.");

    public Task<IReadOnlyList<RiderRow>> ListAsync(long? hubId, CancellationToken cancellationToken = default)
    {
        return RowsAsync(hubId is { } only ? db.Riders.Where(r => r.HubId == only) : db.Riders, cancellationToken);
    }

    /// <summary>
    /// One rider for the admin: who they are and what they carry now, and over <paramref name="kind"/> their runs, stops,
    /// deliveries and the cash they collected against the cash they handed in.
    /// </summary>
    public async Task<Result<RiderDetails>> DetailsAsync(
        long id,
        PeriodKind kind,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken = default)
    {
        var rider = (await RowsAsync(db.Riders.Where(r => r.Id == id), cancellationToken)).SingleOrDefault();
        if (rider is null)
        {
            return NotFound;
        }

        var tenant = tenantContext.Require();
        var period = Period.Of(kind, tenant.Today(time.GetUtcNow().UtcDateTime), from, to);
        if (period.IsFailure)
        {
            return period.Error!;
        }

        var (first, last) = (period.Value.From, period.Value.To);
        var runs = await (
            from run in db.DeliveryRuns
            join hub in db.Hubs on run.HubId equals hub.Id
            where run.RiderId == id && run.RunDate >= first && run.RunDate <= last
            orderby run.RunDate descending, run.Id descending
            select new RunLine(
                run.Id,
                run.RunDate,
                hub.Code,
                run.Status,
                db.DeliveryAttempts.Count(a => a.RunId == run.Id),
                db.DeliveryAttempts.Count(a => a.RunId == run.Id &&
                    (a.Outcome == AttemptOutcome.Delivered || a.Outcome == AttemptOutcome.PartlyDelivered)),
                db.DeliveryAttempts.Count(a => a.RunId == run.Id &&
                    (a.Outcome == AttemptOutcome.Hold || a.Outcome == AttemptOutcome.Refused)),
                db.DeliveryAttempts.Where(a => a.RunId == run.Id).Sum(a => (decimal?)a.CollectedAmount) ?? 0,
                run.CashExpected,
                run.CashReceived))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var attempts = await (
            from attempt in db.DeliveryAttempts
            join run in db.DeliveryRuns on attempt.RunId equals run.Id
            join parcel in db.Parcels on attempt.ParcelId equals parcel.Id
            where attempt.RiderId == id && run.RunDate >= first && run.RunDate <= last
            orderby attempt.Id descending
            select new { parcel.TrackingCode, attempt.AssignedOn, attempt.Outcome, attempt.CollectedAmount, attempt.Reason, attempt.CompletedOn })
            .Take(MaxAttemptsListed)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return new RiderDetails(
            rider,
            period.Value,
            runs,
            [
                .. attempts.Select(a => new AttemptLine(
                    a.TrackingCode,
                    tenant.Local(a.CompletedOn ?? a.AssignedOn),
                    a.Outcome,
                    a.CollectedAmount,
                    a.Reason))
            ]);
    }

    /// <summary>The most of a rider's hand-overs listed on their page, the latest first.</summary>
    public const int MaxAttemptsListed = 30;

    private async Task<IReadOnlyList<RiderRow>> RowsAsync(IQueryable<Rider> riders, CancellationToken cancellationToken)
    {
        var tenant = tenantContext.Require();
        var todayStart = tenant.StartUtc(tenant.Today(time.GetUtcNow().UtcDateTime));
        var rows = await (
            from rider in riders
            join hub in db.Hubs on rider.HubId equals hub.Id
            orderby rider.Archived, hub.Name, rider.Name
            select new
            {
                rider.UserId,
                Row = new RiderRow(
                    rider.Id,
                    rider.Name,
                    rider.Phone,
                    hub.Id,
                    hub.Name,
                    rider.Vehicle,
                    null,
                    !rider.Archived,
                    db.DeliveryAttempts.Count(a => a.RiderId == rider.Id && a.Outcome == null),
                    db.DeliveryAttempts.Count(a => a.RiderId == rider.Id && a.CompletedOn >= todayStart &&
                        (a.Outcome == AttemptOutcome.Delivered || a.Outcome == AttemptOutcome.PartlyDelivered)),
                    (from a in db.DeliveryAttempts
                        join run in db.DeliveryRuns on a.RunId equals run.Id
                        where run.RiderId == rider.Id && run.Status == RunStatus.Open
                        select (decimal?)a.CollectedAmount).Sum() ?? 0)
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var emails = await accounts.EmailsAsync([.. rows.Select(r => r.UserId).OfType<long>()], cancellationToken);

        return [.. rows.Select(r => r.Row with { Email = r.UserId is { } id ? emails.GetValueOrDefault(id) : null })];
    }

    public async Task<Result> AddAsync(NewRider spec, CancellationToken cancellationToken = default)
    {
        if (!await db.Hubs.AnyAsync(h => h.Id == spec.HubId && !h.Archived, cancellationToken))
        {
            return Error.Validation("rider.hub", "Choose the hub the rider works from.");
        }

        var rider = Rider.Create(spec.HubId, spec.Name, spec.Phone, spec.Vehicle, null);
        if (rider.IsFailure)
        {
            return rider.Error!;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var login = await accounts.CreateAsync(new NewLogin(spec.Email, spec.Password, rider.Value.Name, Roles.Rider), cancellationToken);
        if (login.IsFailure)
        {
            return login.Error!;
        }

        rider.Value.LinkLogin(login.Value);
        db.Riders.Add(rider.Value);
        db.OutboxMessages.Add(OutboxContracts.Write(new AccountEmailMessage(login.Value, AccountEmailKind.LoginCreated)));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Result.Success();
    }

    public async Task<Result> EditAsync(
        long id,
        string? name,
        string? phone,
        long hubId,
        Vehicle vehicle,
        CancellationToken cancellationToken = default)
    {
        var rider = await db.Riders.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (rider is null)
        {
            return NotFound;
        }

        if (!await db.Hubs.AnyAsync(h => h.Id == hubId && !h.Archived, cancellationToken))
        {
            return Error.Validation("rider.hub", "Choose the hub the rider works from.");
        }

        if (hubId != rider.HubId && await BusyAsync(rider, "moved to another hub", cancellationToken) is { } busy)
        {
            return busy;
        }

        // What they carry and the pickups they were sent on were sized for the old vehicle
        if (vehicle != rider.Vehicle && await BusyAsync(rider, "given another vehicle", cancellationToken) is { } loaded)
        {
            return loaded;
        }

        var changed = rider.Change(hubId, name, phone, vehicle);
        if (changed.IsSuccess)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }

    public async Task<Result> SetActiveAsync(long id, bool active, CancellationToken cancellationToken = default)
    {
        var rider = await db.Riders.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (rider is null)
        {
            return NotFound;
        }

        if (active)
        {
            rider.Reactivate();
        }
        else
        {
            if (await BusyAsync(rider, "stopped", cancellationToken) is { } busy)
            {
                return busy;
            }

            rider.Deactivate();
        }

        await db.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    /// <summary>
    /// Why the rider cannot leave their hub yet: a stopped rider cannot sign in and a moved one hands their cash in at the
    /// wrong hub, so parcels with them, an open run or a pickup they were sent on would be stuck.
    /// </summary>
    private async Task<Error?> BusyAsync(Rider rider, string what, CancellationToken cancellationToken)
    {
        var parcels = await db.Parcels.CountAsync(p => p.RiderId == rider.Id, cancellationToken);
        var runs = await db.DeliveryRuns.CountAsync(r => r.RiderId == rider.Id && r.Status == RunStatus.Open, cancellationToken);
        var pickups = await db.PickupRequests.CountAsync(r => r.RiderId == rider.Id && r.Status == PickupStatus.Assigned, cancellationToken);
        if (parcels + runs + pickups == 0)
        {
            return null;
        }

        List<string> open = [];
        if (parcels > 0)
        {
            open.Add($"{parcels} parcel{(parcels == 1 ? "" : "s")} with them");
        }

        if (runs > 0)
        {
            open.Add($"{runs} run{(runs == 1 ? "" : "s")} not closed");
        }

        if (pickups > 0)
        {
            open.Add($"{pickups} pickup{(pickups == 1 ? "" : "s")} to make");
        }

        return Error.Conflict(
            "rider.busy",
            $"{rider.Name} cannot be {what} yet: {string.Join(", ", open)}. Close their run at the hub and give their " +
            "pickups to another rider first.");
    }
}
