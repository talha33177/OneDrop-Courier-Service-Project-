using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Application.Delivery.Pickups;
using Application.Parcels.Browse;
using Domain.Delivery;
using Domain.Merchants;
using Domain.Parcels;
using Domain.Payments;

namespace Application.Dashboards;

/// <summary>Parcels booked, delivered and returned on one of the tenant's days.</summary>
public sealed record DayCount(DateOnly Day, int Booked, int Delivered, int Returned);

/// <summary>How many parcels are in each stage of their life.</summary>
public sealed record StageCounts(
    int Pending,
    int InTransit,
    int OutForDelivery,
    int OnHold,
    int Delivered,
    int PartlyDelivered,
    int Returns,
    int Cancelled)
{
    public int Total => Pending + InTransit + OutForDelivery + OnHold + Delivered + PartlyDelivered + Returns + Cancelled;

    public int Active => Pending + InTransit + OutForDelivery + OnHold;

    /// <summary>Handed over at the door, in full or in part, out of the parcels that have ended, as a whole per cent.</summary>
    public int? SuccessRate => Delivered + PartlyDelivered + Returns == 0
        ? null
        : (int)Math.Round(100m * (Delivered + PartlyDelivered) / (Delivered + PartlyDelivered + Returns));
}

/// <summary>
/// The cash riding on the parcels in each stage: what is still expected from the recipient while a parcel is on its
/// way, and what was collected once it arrived. A returned parcel collects nothing, so it has no line of its own.
/// </summary>
public sealed record StageCod(
    decimal Pending,
    decimal InTransit,
    decimal OutForDelivery,
    decimal OnHold,
    decimal Delivered,
    decimal PartlyDelivered)
{
    /// <summary>Cash still to come in: every parcel that has not finished.</summary>
    public decimal OnTheWay => Pending + InTransit + OutForDelivery + OnHold;
}

public sealed record MerchantDashboard(
    string Merchant,
    MerchantStatus Status,
    bool HasPayoutAccount,
    StageCounts Stages,
    decimal CollectedLast30Days,
    decimal Unpaid,
    decimal PaidThisMonth,
    IReadOnlyList<DayCount> Week,
    IReadOnlyList<ParcelRow> Recent)
{
    /// <summary>Parcels waiting for pickup that go with today's pickup: the van has not been to their pickup point yet.</summary>
    public int PickupToday { get; init; }

    /// <summary>Parcels waiting at a point the van has already been to today, which go with the next day's pickup.</summary>
    public int PickupLater { get; init; }

    /// <summary>The courier has collected from the merchant, or the merchant has dropped a parcel at a hub, at least once.</summary>
    public bool HandedOver { get; init; }

    /// <summary>Parcels still on their way after the day they were due.</summary>
    public int Late { get; init; }

    /// <summary>The cash sitting in each stage, shown beside its count.</summary>
    public StageCod Cod { get; init; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>The problems hubs have flagged on the merchant's parcels, still to be cleared.</summary>
    public IssueCounts Issues { get; init; } = new(0, 0);

    /// <summary>Return lists a rider handed over that the merchant has still to confirm it received.</summary>
    public int ReturnsToConfirm { get; init; }

    /// <summary>Parcels booked today, yesterday and in the last 30 days (today included), by the courier's day.</summary>
    public BookedCounts Booked { get; init; } = new(0, 0, 0);

    /// <summary>The latest payout that reached the merchant, if any. Its time is the tenant's.</summary>
    public LastPayout? LastPayout { get; init; }
}

public sealed record BookedCounts(int Today, int Yesterday, int Last30Days);

/// <summary>Parcels flagged in review and exceptional, still to be cleared.</summary>
public sealed record IssueCounts(int InReview, int Exceptional)
{
    public int Total => InReview + Exceptional;
}

public sealed record LastPayout(string Number, decimal Amount, DateTime PaidOn);

public sealed record HubRow(string Code, string Name, int AtHub, int ToAssign, int Incoming, int WithRiders);

public sealed record TopMerchant(string Name, int Parcels, int Delivered);

/// <summary>What the courier has set up, in the order it is set up: a parcel needs all of it to travel.</summary>
public sealed record CourierSetup(int Hubs, int Areas, int Rates, int Riders, int ActiveMerchants)
{
    /// <summary>Riders on a pickup van: without one nobody collects from a merchant, so no parcel ever starts.</summary>
    public int PickupVans { get; init; }

    public bool Done => Hubs > 0 && Areas > 0 && Rates > 0 && Riders > 0 && PickupVans > 0 && ActiveMerchants > 0;
}

public sealed record AdminDashboard(
    int BookedToday,
    int PickedUpToday,
    int DeliveredToday,
    int ReturnedToday,
    StageCounts Stages,
    int PendingMerchants,
    int OpenPickups,
    decimal CashWithRiders,
    decimal OwedToMerchants,
    IReadOnlyList<DayCount> Week,
    IReadOnlyList<HubRow> Hubs,
    IReadOnlyList<TopMerchant> TopMerchants)
{
    /// <summary>Parcels still on their way after the day they were due.</summary>
    public int Late { get; init; }

    /// <summary>The cash sitting in each stage, shown beside its count.</summary>
    public StageCod Cod { get; init; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>The problems hubs have flagged for the courier to look at.</summary>
    public IssueCounts Issues { get; init; } = new(0, 0);

    /// <summary>Merchants' requests about parcels on their way, still to be answered.</summary>
    public int OpenRequests { get; init; }

    /// <summary>Pickups asked for that no hub has given a rider yet.</summary>
    public int PickupsWithoutRider { get; init; }

    /// <summary>Riders' runs still to be closed with the cash handed in.</summary>
    public int OpenRuns { get; init; }

    public CourierSetup Setup { get; init; } = new(0, 0, 0, 0, 0);
}

/// <summary>
/// The numbers on the merchant's and the admin's dashboards, worked out from the data when asked. A merchant's are its
/// own (the merchant filter); the admin's are the whole courier's.
/// </summary>
public class DashboardHandler(
    IAppDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    ParcelListHandler parcels,
    TimeProvider time)
{
    public const int WeekDays = 7;

    public async Task<MerchantDashboard> MerchantAsync(CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var merchantId = currentUser.MerchantId ?? throw new InvalidOperationException("A merchant reads its dashboard.");
        var merchant = await db.Merchants.AsNoTracking().SingleAsync(m => m.Id == merchantId, cancellationToken);
        var today = tenant.Today(time.GetUtcNow().UtcDateTime);
        var since30 = tenant.StartUtc(today.AddDays(-29));
        var monthStart = tenant.StartUtc(new DateOnly(today.Year, today.Month, 1));

        var collected = await db.Parcels
            .Where(p => p.ClosedOn >= since30 && p.CollectedAmount != null)
            .SumAsync(p => p.CollectedAmount, cancellationToken) ?? 0;
        var unpaid = await db.LedgerEntries.Where(e => e.PayoutId == null).SumAsync(e => (decimal?)e.Amount, cancellationToken) ?? 0;
        var paid = await db.Payouts
            .Where(p => p.Status == PayoutStatus.Paid && p.PaidOn >= monthStart)
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0;
        var stages = await StagesAsync(cancellationToken);
        var recent = await parcels.ListAsync(new ParcelQuery { PageSize = 8 }, cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var visited = db.PickupRequests.MadeToday(tenant, now).Select(r => r.PickupPointId);
        var pickupLater = await db.Parcels.CountAsync(p => p.Status == ParcelStatus.Pending && visited.Contains(p.PickupPointId), cancellationToken);
        var todayStart = tenant.StartUtc(today);
        var yesterdayStart = tenant.StartUtc(today.AddDays(-1));
        var booked = new BookedCounts(
            await db.Parcels.CountAsync(p => p.Created >= todayStart, cancellationToken),
            await db.Parcels.CountAsync(p => p.Created >= yesterdayStart && p.Created < todayStart, cancellationToken),
            await db.Parcels.CountAsync(p => p.Created >= since30, cancellationToken));
        var lastPayout = await db.Payouts
            .Where(p => p.Status == PayoutStatus.Paid)
            .OrderByDescending(p => p.PaidOn)
            .Select(p => new LastPayout(p.Number, p.Amount, p.PaidOn!.Value))
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        var handedOver = await db.Parcels.AnyAsync(p => p.Status != ParcelStatus.Pending && p.Status != ParcelStatus.Cancelled, cancellationToken);

        return new MerchantDashboard(
            merchant.Name,
            merchant.Status,
            merchant.HasPayoutAccount,
            stages.Counts,
            collected,
            unpaid,
            paid,
            await WeekAsync(tenant, today, cancellationToken),
            recent.Page.Items)
        {
            PickupToday = stages.Counts.Pending - pickupLater,
            PickupLater = pickupLater,
            HandedOver = handedOver,
            Late = await LateAsync(today, cancellationToken),
            Cod = stages.Cod,
            Issues = await IssuesAsync(cancellationToken),
            ReturnsToConfirm = await db.ReturnLists.CountAsync(l => l.Status == ReturnListStatus.HandedOver, cancellationToken),
            Booked = booked,
            LastPayout = lastPayout is null ? null : lastPayout with { PaidOn = tenant.Local(lastPayout.PaidOn) }
        };
    }

    public async Task<AdminDashboard> AdminAsync(CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var today = tenant.Today(time.GetUtcNow().UtcDateTime);
        var todayStart = tenant.StartUtc(today);

        var bookedToday = await db.Parcels.CountAsync(p => p.Created >= todayStart, cancellationToken);
        var pickedToday = await db.ParcelEvents
            .Where(e => e.Status == ParcelStatus.PickedUp && e.Created >= todayStart)
            .Select(e => e.ParcelId)
            .Distinct()
            .CountAsync(cancellationToken);
        var deliveredToday = await db.Parcels.CountAsync(
            p => p.ClosedOn >= todayStart && (p.Status == ParcelStatus.Delivered || p.Status == ParcelStatus.PartlyDelivered),
            cancellationToken);
        var returnedToday = await db.Parcels.CountAsync(p => p.ClosedOn >= todayStart && p.Status == ParcelStatus.Returned, cancellationToken);
        var pendingMerchants = await db.Merchants.CountAsync(m => m.Status == MerchantStatus.Pending && !m.Archived, cancellationToken);
        var openPickups = await db.PickupRequests.CountAsync(
            r => r.PickupDate <= today && (r.Status == PickupStatus.Scheduled || r.Status == PickupStatus.Assigned),
            cancellationToken);
        var cash = await (
            from attempt in db.DeliveryAttempts
            join run in db.DeliveryRuns on attempt.RunId equals run.Id
            where run.Status == RunStatus.Open
            select (decimal?)attempt.CollectedAmount)
            .SumAsync(cancellationToken) ?? 0;
        // What the courier owes: each merchant's unpaid balance that is above nothing. A merchant that owes the courier
        // is a debt the other way, not less owed to everyone else
        var owed = await db.LedgerEntries
            .Where(e => e.PayoutId == null)
            .GroupBy(e => e.MerchantId)
            .Select(g => g.Sum(e => e.Amount))
            .Where(balance => balance > 0)
            .SumAsync(balance => (decimal?)balance, cancellationToken) ?? 0;

        var hubs = await db.Hubs
            .Where(h => !h.Archived)
            .OrderBy(h => h.Name)
            .Select(h => new HubRow(
                h.Code,
                h.Name,
                db.Parcels.Count(p => p.CurrentHubId == h.Id),
                db.Parcels.Count(p => p.CurrentHubId == h.Id && p.DeliveryHubId == h.Id &&
                    (p.Status == ParcelStatus.AtHub || p.Status == ParcelStatus.OnHold) &&
                    (p.HoldUntil == null || p.HoldUntil <= today)),
                db.Parcels.Count(p => p.TransferToHubId == h.Id ||
                    (p.Status == ParcelStatus.PickedUp && p.PickupHubId == h.Id)),
                (from a in db.DeliveryAttempts
                    join run in db.DeliveryRuns on a.RunId equals run.Id
                    where run.HubId == h.Id && run.Status == RunStatus.Open && a.Outcome == null
                    select a.Id).Count()))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var stages = await StagesAsync(cancellationToken);
        var monthStart = tenant.StartUtc(new DateOnly(today.Year, today.Month, 1));
        var busiest = await db.Parcels
            .Where(parcel => parcel.Created >= monthStart)
            .GroupBy(parcel => parcel.MerchantId)
            .Select(parcels => new
            {
                MerchantId = parcels.Key,
                Booked = parcels.Count(),
                Delivered = parcels.Count(p => p.Status == ParcelStatus.Delivered || p.Status == ParcelStatus.PartlyDelivered)
            })
            .OrderByDescending(m => m.Booked)
            .Take(5)
            .ToListAsync(cancellationToken);
        var busiestIds = busiest.Select(m => m.MerchantId).ToList();
        var names = await db.Merchants
            .Where(m => busiestIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.Name, cancellationToken);
        var top = busiest.Select(m => new TopMerchant(names[m.MerchantId], m.Booked, m.Delivered)).ToList();

        return new AdminDashboard(
            bookedToday,
            pickedToday,
            deliveredToday,
            returnedToday,
            stages.Counts,
            pendingMerchants,
            openPickups,
            cash,
            owed,
            await WeekAsync(tenant, today, cancellationToken),
            hubs.Where(h => h.AtHub + h.Incoming + h.WithRiders > 0).ToList() is { Count: > 0 } busy ? busy : hubs.Take(6).ToList(),
            top)
        {
            Late = await LateAsync(today, cancellationToken),
            Cod = stages.Cod,
            Issues = await IssuesAsync(cancellationToken),
            OpenRequests = await db.ParcelRequests.CountAsync(r => r.Status == ParcelRequestStatus.Open, cancellationToken),
            PickupsWithoutRider = await db.PickupRequests.CountAsync(r => r.Status == PickupStatus.Scheduled && r.PickupDate <= today, cancellationToken),
            OpenRuns = await db.DeliveryRuns.CountAsync(r => r.Status == RunStatus.Open, cancellationToken),
            Setup = new CourierSetup(
                await db.Hubs.CountAsync(h => !h.Archived, cancellationToken),
                await db.Areas.CountAsync(a => !a.Archived, cancellationToken),
                await db.DeliveryRates.CountAsync(cancellationToken),
                await db.Riders.CountAsync(r => !r.Archived, cancellationToken),
                await db.Merchants.CountAsync(m => m.Status == MerchantStatus.Active && !m.Archived, cancellationToken))
            {
                PickupVans = await db.Riders.CountAsync(r => !r.Archived && r.Vehicle == Vehicle.Van, cancellationToken)
            }
        };
    }

    private async Task<int> LateAsync(DateOnly today, CancellationToken cancellationToken)
    {
        return await db.Parcels.CountAsync(p => p.DueOn < today && ParcelStatuses.ToDeliver.Contains(p.Status), cancellationToken);
    }

    private async Task<IssueCounts> IssuesAsync(CancellationToken cancellationToken)
    {
        var flagged = await db.Parcels
            .Where(p => p.Issue != null)
            .GroupBy(p => p.Issue)
            .Select(g => new { Issue = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Issue!.Value, g => g.Count, cancellationToken);

        return new IssueCounts(flagged.GetValueOrDefault(ParcelIssue.InReview), flagged.GetValueOrDefault(ParcelIssue.Exceptional));
    }

    private async Task<(StageCounts Counts, StageCod Cod)> StagesAsync(CancellationToken cancellationToken)
    {
        var byStatus = await db.Parcels
            .GroupBy(p => p.Status)
            .Select(g => new
            {
                Status = g.Key,
                Count = g.Count(),
                Cod = g.Sum(p => p.CodAmount),
                Collected = g.Sum(p => (decimal?)p.CollectedAmount) ?? 0
            })
            .ToDictionaryAsync(g => g.Status, g => g, cancellationToken);

        int Count(ParcelTab tab)
        {
            return ParcelListHandler.StatusesOf(tab).Sum(status => byStatus.GetValueOrDefault(status)?.Count ?? 0);
        }

        // On the way the cash is what the recipient still owes; once delivered it is what the rider actually took.
        decimal Expected(ParcelTab tab)
        {
            return ParcelListHandler.StatusesOf(tab).Sum(status => byStatus.GetValueOrDefault(status)?.Cod ?? 0);
        }

        decimal Collected(ParcelTab tab)
        {
            return ParcelListHandler.StatusesOf(tab).Sum(status => byStatus.GetValueOrDefault(status)?.Collected ?? 0);
        }

        var counts = new StageCounts(
            Count(ParcelTab.Pending),
            Count(ParcelTab.InTransit),
            Count(ParcelTab.OutForDelivery),
            Count(ParcelTab.OnHold),
            Count(ParcelTab.Delivered),
            Count(ParcelTab.PartlyDelivered),
            Count(ParcelTab.Returns),
            Count(ParcelTab.Cancelled));

        var cod = new StageCod(
            Expected(ParcelTab.Pending),
            Expected(ParcelTab.InTransit),
            Expected(ParcelTab.OutForDelivery),
            Expected(ParcelTab.OnHold),
            Collected(ParcelTab.Delivered),
            Collected(ParcelTab.PartlyDelivered));

        return (counts, cod);
    }

    private async Task<IReadOnlyList<DayCount>> WeekAsync(TenantInfo tenant, DateOnly today, CancellationToken cancellationToken)
    {
        var first = today.AddDays(1 - WeekDays);
        var since = tenant.StartUtc(first);
        var rows = await db.Parcels
            .Where(p => p.Created >= since || p.ClosedOn >= since)
            .Select(p => new { p.Created, p.ClosedOn, p.Status })
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var booked = rows.Where(r => r.Created >= since).GroupBy(r => tenant.Today(r.Created)).ToDictionary(g => g.Key, g => g.Count());
        var closed = rows
            .Where(r => r.ClosedOn >= since)
            .GroupBy(r => tenant.Today(r.ClosedOn!.Value))
            .ToDictionary(g => g.Key, g => g.ToList());

        return
        [
            .. Enumerable.Range(0, WeekDays)
                .Select(first.AddDays)
                .Select(day => new DayCount(
                    day,
                    booked.GetValueOrDefault(day),
                    closed.GetValueOrDefault(day)?.Count(r => r.Status is ParcelStatus.Delivered or ParcelStatus.PartlyDelivered) ?? 0,
                    closed.GetValueOrDefault(day)?.Count(r => r.Status == ParcelStatus.Returned) ?? 0))
        ];
    }
}
