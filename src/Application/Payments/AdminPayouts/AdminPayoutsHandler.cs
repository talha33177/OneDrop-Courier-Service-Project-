using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Application.Payments.MerchantPayments;
using Application.Payments.RunPayouts;
using Domain.Payments;

namespace Application.Payments.AdminPayouts;

/// <summary>
/// A merchant's balance not yet paid out. <see cref="Payable"/> is the part the next run pays: the lines up to yesterday.
/// The rest belongs to today's parcels and is paid from tomorrow. <see cref="Adjustments"/> are the lines the courier
/// wrote by hand; <see cref="Hold"/> says why its payouts are held, when they are.
/// </summary>
public sealed record MerchantBalance(long MerchantId, string Merchant, decimal Cod, decimal Charges, bool HasPayoutAccount, decimal Payable)
{
    public decimal Adjustments { get; init; }

    public string? Hold { get; init; }

    public decimal Net => Cod - Charges + Adjustments;

    public decimal DueTomorrow => Net - Payable;

    /// <summary>The next run pays it: money up to yesterday, somewhere to send it, and no hold.</summary>
    public bool IsPaidNext => Payable > 0 && HasPayoutAccount && Hold is null;

    /// <summary>The merchant owes the courier: its charges come to more than its cash.</summary>
    public bool OwesCourier => Net < 0;
}

public sealed record PayoutsOverview(
    IReadOnlyList<MerchantBalance> Owed,
    IReadOnlyList<PayoutRow> Payouts,
    IReadOnlyList<PayoutRow> Stuck,
    decimal PaidThisMonth,
    decimal PendingTotal,
    ScheduledTimes Schedule)
{
    /// <summary>The hourly run has not run for over two hours, or never: the scheduler, or the app, is not running.</summary>
    public bool RunIsLate { get; init; }

    /// <summary>Which page of the payouts list this is, how many pages, and how many payouts in all.</summary>
    public (int Number, int Pages, int Total) PayoutsPage { get; init; } = (1, 1, 0);

    /// <summary>What the next run sends: each merchant it pays, its lines up to yesterday.</summary>
    public decimal PayableNow => Owed.Where(m => m.IsPaidNext).Sum(m => m.Payable);

    /// <summary>Merchants the courier owes money to, now or from tomorrow.</summary>
    public IEnumerable<MerchantBalance> ToPay => Owed.Where(m => m.Net > 0);

    /// <summary>Merchants whose charges are more than their cash: nothing is paid until their cash or an adjustment covers it.</summary>
    public IEnumerable<MerchantBalance> OwingCourier => Owed.Where(m => m.OwesCourier).OrderBy(m => m.Net);
}

/// <summary>
/// The courier's payouts: what it owes each merchant now and who owes it, payouts the gateway keeps refusing, the
/// payouts made (newest first) and when the hourly run last ran. Courier admins only.
/// </summary>
public class AdminPayoutsHandler(
    IAppDbContext db,
    ITenantContext tenantContext,
    MerchantPaymentsHandler payments,
    IJobSchedule schedule,
    TimeProvider time)
{
    /// <summary>How many payouts a page of the list shows.</summary>
    public const int PayoutsShown = 25;

    public async Task<PayoutsOverview> GetAsync(PayoutStatus? status, int page = 1, CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var upTo = tenant.Today(time.GetUtcNow().UtcDateTime).AddDays(-1);
        var balances = await db.LedgerEntries
            .Where(line => line.PayoutId == null)
            .GroupBy(line => line.MerchantId)
            .Select(lines => new
            {
                MerchantId = lines.Key,
                Cod = lines.Sum(l => l.Kind == LedgerEntryKind.Cod ? l.Amount : 0),
                Charges = -lines.Sum(l => l.Kind != LedgerEntryKind.Cod && l.Kind != LedgerEntryKind.Adjustment ? l.Amount : 0),
                Adjustments = lines.Sum(l => l.Kind == LedgerEntryKind.Adjustment ? l.Amount : 0),
                Payable = lines.Sum(l => l.EntryDate <= upTo ? l.Amount : 0)
            })
            .ToListAsync(cancellationToken);
        var merchantIds = balances.Select(b => b.MerchantId).ToList();
        var merchants = await db.Merchants
            .Where(m => merchantIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Name, HasAccount = m.PayoutMethod != null, m.PayoutHold })
            .ToDictionaryAsync(m => m.Id, cancellationToken);
        var owed = balances
            .Select(b => new MerchantBalance(b.MerchantId, merchants[b.MerchantId].Name, b.Cod, b.Charges, merchants[b.MerchantId].HasAccount, b.Payable)
            {
                Adjustments = b.Adjustments,
                Hold = merchants[b.MerchantId].PayoutHold
            })
            .Where(b => b.Net != 0)
            .ToList();

        var payouts = db.Payouts.AsQueryable();
        if (status is { } only)
        {
            payouts = payouts.Where(p => p.Status == only);
        }

        var made = await payouts.CountAsync(cancellationToken);
        var number = Math.Clamp(page, 1, Math.Max(1, (made + PayoutsShown - 1) / PayoutsShown));

        var today = tenant.Today(time.GetUtcNow().UtcDateTime);
        var monthStart = tenant.StartUtc(new DateOnly(today.Year, today.Month, 1));
        var paidThisMonth = await db.Payouts
            .Where(p => p.Status == PayoutStatus.Paid && p.PaidOn >= monthStart)
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0;
        var pending = await db.Payouts
            .Where(p => p.Status == PayoutStatus.Pending)
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0;
        var times = await schedule.TimesAsync(PayoutsJob.RecurringId, cancellationToken);

        return new PayoutsOverview(
            [.. owed.OrderByDescending(m => m.Net)],
            await payments.RowsAsync(payouts.OrderByDescending(p => p.Id).Skip((number - 1) * PayoutsShown).Take(PayoutsShown), tenant, cancellationToken),
            await payments.RowsAsync(
                db.Payouts.Where(p => p.Status == PayoutStatus.Pending && p.FailedAttempts > 0).OrderBy(p => p.Id),
                tenant,
                cancellationToken),
            paidThisMonth,
            pending,
            new ScheduledTimes(
                times.LastRun is { } last ? tenant.Local(last) : null,
                times.NextRun is { } next ? tenant.Local(next) : null))
        {
            RunIsLate = times.LastRun is not { } ran || time.GetUtcNow().UtcDateTime - ran > TimeSpan.FromHours(2),
            PayoutsPage = (number, Math.Max(1, (made + PayoutsShown - 1) / PayoutsShown), made)
        };
    }
}
