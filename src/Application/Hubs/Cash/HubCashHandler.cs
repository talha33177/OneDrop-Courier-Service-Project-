using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Domain.Common;
using Domain.Delivery;
using Domain.Payments;

namespace Application.Hubs.Cash;

/// <summary>A run closed with less cash than the rider collected, and what the hub manager did about it. Times are the courier's.</summary>
public sealed record ShortRun(
    long RunId,
    string HubCode,
    string Rider,
    DateOnly RunDate,
    decimal Expected,
    decimal Received,
    DateTime ClosedOn,
    string? AcceptedBy,
    DateTime? AcceptedOn,
    string? Note)
{
    public decimal Short => Expected - Received;

    public bool Waiting => AcceptedOn is null;
}

/// <summary>A deposit as the lists show it. Times are the courier's.</summary>
public sealed record DepositRow(
    string Number,
    string HubCode,
    string Hub,
    decimal Amount,
    string Bank,
    string SlipNumber,
    DateOnly DepositedOn,
    CashDepositStatus Status,
    string? RecordedBy,
    DateTime Recorded,
    string? AnsweredBy,
    DateTime? AnsweredOn,
    string? Answer);

/// <summary>
/// A hub's cash: what its riders handed in at closing, what it paid into the bank (refused deposits not counted), the
/// difference it holds now, the shortfalls waiting for the hub manager and the deposits of the last month.
/// </summary>
public sealed record HubCash(
    decimal HandedIn,
    decimal Deposited,
    decimal Confirmed,
    IReadOnlyList<ShortRun> Shortfalls,
    IReadOnlyList<DepositRow> Deposits,
    DateOnly Today)
{
    public decimal InHand => HandedIn - Deposited;

    public decimal AwaitingFinance => Deposited - Confirmed;
}

/// <summary>One hub's line on finance's page.</summary>
public sealed record HubCashRow(string Code, string Name, decimal InHand, decimal AwaitingFinance, int Shortfalls, decimal ShortAmount);

/// <summary>Every hub's cash for finance: what each holds, the deposits to confirm, the latest, and the shortfalls waiting.</summary>
public sealed record CourierCash(
    IReadOnlyList<HubCashRow> Hubs,
    IReadOnlyList<DepositRow> ToConfirm,
    IReadOnlyList<DepositRow> Recent,
    IReadOnlyList<ShortRun> Shortfalls)
{
    public decimal InHand => Hubs.Sum(h => h.InHand);
}

/// <summary>
/// The cash a hub holds after closing its riders' runs, and where it goes. The cash desk pays it into the courier's bank
/// and records each deposit with its slip; finance confirms it once the money shows in the bank, or refuses it, which
/// makes the cash the hub's again. A run closed short waits for the hub manager to accept the shortfall and say what was
/// done. A hub's staff see their own hub; finance sees every hub.
/// </summary>
public class HubCashHandler(
    IAppDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IUserAccounts accounts,
    HubDirectory hubs,
    TimeProvider time)
{
    public static readonly Error NotFound = Error.NotFound("cash.notFound", "That was not found at this hub.");

    /// <summary>How far back a hub sees its deposits and the shortfalls already settled.</summary>
    public const int RecentDays = 31;

    public async Task<HubCash?> HubAsync(string hubCode, CancellationToken cancellationToken = default)
    {
        var hub = await hubs.FindAsync(hubCode, cancellationToken);
        if (hub is null)
        {
            return null;
        }

        var totals = await TotalsAsync(hub.Id, cancellationToken);
        var since = time.GetUtcNow().UtcDateTime.AddDays(-RecentDays);

        return new HubCash(
            totals.GetValueOrDefault(hub.Id).HandedIn,
            totals.GetValueOrDefault(hub.Id).Deposited,
            totals.GetValueOrDefault(hub.Id).Confirmed,
            await ShortsAsync(db.DeliveryRuns.Where(r => r.HubId == hub.Id && (r.ShortAcceptedOn == null || r.ShortAcceptedOn >= since)), cancellationToken),
            await DepositsAsync(db.CashDeposits.Where(d => d.HubId == hub.Id && (d.Status == CashDepositStatus.Recorded || d.Created >= since)), cancellationToken),
            tenantContext.Require().Today(time.GetUtcNow().UtcDateTime));
    }

    /// <summary>The cash desk paid <paramref name="amount"/> of the hub's cash into the bank; returns the deposit's number.</summary>
    public async Task<Result<string>> DepositAsync(
        string hubCode,
        decimal amount,
        string? bank,
        string? slipNumber,
        DateOnly depositedOn,
        CancellationToken cancellationToken = default)
    {
        var hub = await hubs.FindAsync(hubCode, cancellationToken);
        if (hub is null)
        {
            return NotFound;
        }

        var slip = slipNumber?.Trim().ToUpperInvariant();
        var again = await db.CashDeposits
            .Where(d => d.SlipNumber == slip && d.Status != CashDepositStatus.Refused)
            .Select(d => d.Number)
            .FirstOrDefaultAsync(cancellationToken);
        if (again is not null)
        {
            return Error.Conflict("deposit.slip.again", $"Slip {slip} is already recorded as deposit {again}.");
        }

        var totals = (await TotalsAsync(hub.Id, cancellationToken)).GetValueOrDefault(hub.Id);
        var recorded = CashDeposit.Record(
            hub.Id,
            amount,
            bank,
            slipNumber,
            depositedOn,
            totals.HandedIn - totals.Deposited,
            tenantContext.Require().Today(time.GetUtcNow().UtcDateTime),
            currentUser.UserId);
        if (recorded.IsFailure)
        {
            return recorded.Error!;
        }

        db.CashDeposits.Add(recorded.Value);
        await db.SaveChangesAsync(cancellationToken);

        return recorded.Value.Number;
    }

    /// <summary>The hub manager accepts the shortfall on run <paramref name="runId"/> of their hub and says what was done.</summary>
    public async Task<Result> AcceptShortAsync(string hubCode, long runId, string? note, CancellationToken cancellationToken = default)
    {
        var hub = await hubs.FindAsync(hubCode, cancellationToken);
        var run = hub is null
            ? null
            : await db.DeliveryRuns.SingleOrDefaultAsync(r => r.Id == runId && r.HubId == hub.Id, cancellationToken);
        if (run is null)
        {
            return NotFound;
        }

        var accepted = run.AcceptShort(note, currentUser.UserId, time.GetUtcNow().UtcDateTime);

        return accepted.IsFailure ? accepted : await SaveAsync(cancellationToken);
    }

    /// <summary>Every hub's cash, for finance.</summary>
    public async Task<CourierCash> CourierAsync(CancellationToken cancellationToken = default)
    {
        var totals = await TotalsAsync(null, cancellationToken);
        var waiting = await db.DeliveryRuns
            .Where(r => r.Status == RunStatus.Closed && r.CashReceived < r.CashExpected && r.ShortAcceptedOn == null)
            .GroupBy(r => r.HubId)
            .Select(g => new { HubId = g.Key, Count = g.Count(), Amount = g.Sum(r => r.CashExpected - r.CashReceived) ?? 0 })
            .ToListAsync(cancellationToken);
        var since = time.GetUtcNow().UtcDateTime.AddDays(-RecentDays);
        var hubRows = await db.Hubs
            .Where(h => !h.Archived)
            .OrderBy(h => h.Name)
            .Select(h => new { h.Id, h.Code, h.Name })
            .ToListAsync(cancellationToken);

        return new CourierCash(
            [
                .. hubRows
                    .Select(h => new HubCashRow(
                        h.Code,
                        h.Name,
                        totals.GetValueOrDefault(h.Id).HandedIn - totals.GetValueOrDefault(h.Id).Deposited,
                        totals.GetValueOrDefault(h.Id).Deposited - totals.GetValueOrDefault(h.Id).Confirmed,
                        waiting.SingleOrDefault(w => w.HubId == h.Id)?.Count ?? 0,
                        waiting.SingleOrDefault(w => w.HubId == h.Id)?.Amount ?? 0))
                    .Where(h => h.InHand != 0 || h.AwaitingFinance != 0 || h.Shortfalls > 0)
            ],
            await DepositsAsync(db.CashDeposits.Where(d => d.Status == CashDepositStatus.Recorded), cancellationToken),
            await DepositsAsync(db.CashDeposits.Where(d => d.Status != CashDepositStatus.Recorded && d.AnsweredOn >= since), cancellationToken),
            await ShortsAsync(db.DeliveryRuns.Where(r => r.ShortAcceptedOn == null), cancellationToken));
    }

    /// <summary>Finance found deposit <paramref name="number"/> in the bank.</summary>
    public Task<Result> ConfirmAsync(string number, CancellationToken cancellationToken = default)
    {
        return AnswerAsync(number, (deposit, now) => deposit.Confirm(currentUser.UserId, now), cancellationToken);
    }

    /// <summary>Finance did not find deposit <paramref name="number"/> as recorded; the cash is the hub's again.</summary>
    public Task<Result> RefuseAsync(string number, string? reason, CancellationToken cancellationToken = default)
    {
        return AnswerAsync(number, (deposit, now) => deposit.Refuse(reason, currentUser.UserId, now), cancellationToken);
    }

    /// <summary>
    /// Per hub (one hub, or every hub when <paramref name="hubId"/> is null): the cash its riders handed in at closing,
    /// what it deposited (refused deposits not counted) and how much of that finance confirmed.
    /// </summary>
    private async Task<Dictionary<long, (decimal HandedIn, decimal Deposited, decimal Confirmed)>> TotalsAsync(
        long? hubId,
        CancellationToken cancellationToken)
    {
        var handedIn = await db.DeliveryRuns
            .Where(r => r.Status == RunStatus.Closed && (hubId == null || r.HubId == hubId))
            .GroupBy(r => r.HubId)
            .Select(g => new { HubId = g.Key, Amount = g.Sum(r => r.CashReceived) ?? 0 })
            .ToListAsync(cancellationToken);
        var deposited = await db.CashDeposits
            .Where(d => d.Status != CashDepositStatus.Refused && (hubId == null || d.HubId == hubId))
            .GroupBy(d => d.HubId)
            .Select(g => new
            {
                HubId = g.Key,
                Amount = g.Sum(d => d.Amount),
                Confirmed = g.Where(d => d.Status == CashDepositStatus.Confirmed).Sum(d => d.Amount)
            })
            .ToListAsync(cancellationToken);

        return handedIn.Select(h => h.HubId)
            .Union(deposited.Select(d => d.HubId))
            .ToDictionary(
                id => id,
                id => (
                    handedIn.SingleOrDefault(h => h.HubId == id)?.Amount ?? 0,
                    deposited.SingleOrDefault(d => d.HubId == id)?.Amount ?? 0,
                    deposited.SingleOrDefault(d => d.HubId == id)?.Confirmed ?? 0));
    }

    /// <summary>The runs among <paramref name="runs"/> closed short, the waiting ones first. Filter before calling.</summary>
    private async Task<IReadOnlyList<ShortRun>> ShortsAsync(IQueryable<DeliveryRun> runs, CancellationToken cancellationToken)
    {
        var tenant = tenantContext.Require();
        var rows = await (
            from run in runs
            join rider in db.Riders on run.RiderId equals rider.Id
            join hub in db.Hubs on run.HubId equals hub.Id
            where run.Status == RunStatus.Closed && run.CashReceived < run.CashExpected
            orderby run.ShortAcceptedOn != null, run.ClosedOn descending
            select new
            {
                run.ShortAcceptedById,
                Row = new ShortRun(
                    run.Id,
                    hub.Code,
                    rider.Name,
                    run.RunDate,
                    run.CashExpected ?? 0,
                    run.CashReceived ?? 0,
                    run.ClosedOn!.Value,
                    null,
                    run.ShortAcceptedOn,
                    run.ShortNote)
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var names = await accounts.NamesAsync([.. rows.Select(r => r.ShortAcceptedById).OfType<long>().Distinct()], cancellationToken);

        return
        [
            .. rows.Select(r => r.Row with
            {
                ClosedOn = tenant.Local(r.Row.ClosedOn),
                AcceptedOn = r.Row.AcceptedOn is { } on ? tenant.Local(on) : null,
                AcceptedBy = r.ShortAcceptedById is { } by ? names.GetValueOrDefault(by) : null
            })
        ];
    }

    /// <summary>The deposits as rows, newest first, with who recorded and answered each. Filter before calling.</summary>
    private async Task<IReadOnlyList<DepositRow>> DepositsAsync(IQueryable<CashDeposit> deposits, CancellationToken cancellationToken)
    {
        var tenant = tenantContext.Require();
        var rows = await (
            from deposit in deposits
            join hub in db.Hubs on deposit.HubId equals hub.Id
            orderby deposit.Id descending
            select new
            {
                deposit.RecordedById,
                deposit.AnsweredById,
                Row = new DepositRow(
                    deposit.Number,
                    hub.Code,
                    hub.Name,
                    deposit.Amount,
                    deposit.Bank,
                    deposit.SlipNumber,
                    deposit.DepositedOn,
                    deposit.Status,
                    null,
                    deposit.Created,
                    null,
                    deposit.AnsweredOn,
                    deposit.Answer)
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var names = await accounts.NamesAsync(
            [.. rows.SelectMany(r => new[] { r.RecordedById, r.AnsweredById }).OfType<long>().Distinct()],
            cancellationToken);

        return
        [
            .. rows.Select(r => r.Row with
            {
                Recorded = tenant.Local(r.Row.Recorded),
                RecordedBy = r.RecordedById is { } recorder ? names.GetValueOrDefault(recorder) : null,
                AnsweredOn = r.Row.AnsweredOn is { } on ? tenant.Local(on) : null,
                AnsweredBy = r.AnsweredById is { } answerer ? names.GetValueOrDefault(answerer) : null
            })
        ];
    }

    private async Task<Result> AnswerAsync(
        string number,
        Func<CashDeposit, DateTime, Result> answer,
        CancellationToken cancellationToken)
    {
        var code = number.Trim().ToUpperInvariant();
        var deposit = await db.CashDeposits.SingleOrDefaultAsync(d => d.Number == code, cancellationToken);
        if (deposit is null)
        {
            return Error.NotFound("deposit.notFound", "That deposit was not found.");
        }

        var answered = answer(deposit, time.GetUtcNow().UtcDateTime);

        return answered.IsFailure ? answered : await SaveAsync(cancellationToken);
    }

    private async Task<Result> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("cash.changed", "Someone changed this just now. Reload the page and try again.");
        }

        return Result.Success();
    }
}
