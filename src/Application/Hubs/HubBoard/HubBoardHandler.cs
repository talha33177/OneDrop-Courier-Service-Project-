using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Application.Pricing.DeliveryOptions;
using Domain.Delivery;
using Domain.Parcels;

namespace Application.Hubs.HubBoard;

/// <summary>
/// A parcel as the hub's lists show it, with any problem flagged on it (which keeps it from a rider), how fast it goes
/// and what is inside (express and food that spoils go first).
/// </summary>
public sealed record BoardParcel(
    string TrackingCode,
    string Merchant,
    string RecipientName,
    string Area,
    decimal CodAmount,
    ParcelStatus Status,
    int Attempts,
    DateOnly? HoldUntil,
    string? Reason,
    DateOnly? DueOn,
    int WeightGrams, // what the hub's scale read, or the merchant's figure until it is weighed
    ParcelIssue? Issue,
    string? IssueNote,
    DeliveryTags Tags)
{
    public bool IsPriority => Tags.GoesFirst;

    /// <summary>The hub the parcel is at, which the board of every hub shows beside it.</summary>
    public string HubCode { get; init; } = "";
}

/// <summary>Parcels at one hub (<c>FromHubCode</c>) that leave for another: forward ones, or returns going back.</summary>
public sealed record DispatchGroup(string FromHubCode, string HubCode, string HubName, bool Returns, IReadOnlyList<BoardParcel> Parcels);

/// <summary>Returning parcels at a hub that go back to one merchant.</summary>
public sealed record HandBackGroup(string HubCode, string Merchant, string MerchantPhone, IReadOnlyList<BoardParcel> Parcels);

/// <summary>
/// The hub's day at a glance: what is coming in, what waits for a rider (the most urgent first) and how much of it is
/// late, what to send to other hubs, what to hand back to merchants, what the riders have out and how many of them are
/// late back. <c>Hub</c> is null on the board of every hub, where each list says which hub its work is at.
/// </summary>
public sealed record HubBoard(
    HubItem? Hub,
    int Incoming,
    int PickupsOpen,
    IReadOnlyList<BoardParcel> ToAssign,
    IReadOnlyList<DispatchGroup> ToDispatch,
    IReadOnlyList<HandBackGroup> ToHandBack,
    int WithRiders,
    int OpenRuns,
    int LateRuns,
    decimal CashWithRiders,
    DateOnly Today)
{
    public int LateToAssign => ToAssign.Count(p => p.DueOn < Today);

    public int ToDispatchCount => ToDispatch.Sum(group => group.Parcels.Count);

    public int ToHandBackCount => ToHandBack.Sum(group => group.Parcels.Count);
}

/// <summary>
/// Works out a hub's board, or one board over every hub of the courier, from the parcels, pickups and runs when asked:
/// nothing is stored.
/// </summary>
public class HubBoardHandler(IAppDbContext db, ITenantContext tenantContext, HubDirectory hubs, TimeProvider time)
{
    public async Task<HubBoard?> GetAsync(string hubCode, CancellationToken cancellationToken = default)
    {
        var hub = await hubs.FindAsync(hubCode, cancellationToken);

        return hub is null ? null : await BuildAsync(hub.Id, new HubItem(hub.Id, hub.Code, hub.Name, hub.Address, hub.Phone), cancellationToken);
    }

    /// <summary>The work waiting at every hub of the courier together.</summary>
    public async Task<HubBoard> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await BuildAsync(null, null, cancellationToken);
    }

    private async Task<HubBoard> BuildAsync(long? hubId, HubItem? hub, CancellationToken cancellationToken)
    {
        var here = await (
            from parcel in db.Parcels
            join merchant in db.Merchants on parcel.MerchantId equals merchant.Id
            join area in db.Areas on parcel.AreaId equals area.Id
            where parcel.CurrentHubId != null && (hubId == null || parcel.CurrentHubId == hubId)
            orderby parcel.Speed!.GoesFirst || parcel.ProductType!.GoesFirst descending,
                parcel.DueOn == null, parcel.DueOn, parcel.Id
            select new
            {
                CurrentHubId = parcel.CurrentHubId!.Value,
                parcel.Status,
                parcel.DeliveryHubId,
                parcel.PickupHubId,
                MerchantPhone = merchant.ContactPhone,
                Row = new BoardParcel(
                    parcel.TrackingCode,
                    merchant.Name,
                    parcel.RecipientName,
                    area.Name,
                    parcel.CodAmount,
                    parcel.Status,
                    parcel.Attempts,
                    parcel.HoldUntil,
                    parcel.Status == ParcelStatus.Returning ? parcel.ReturnReason : parcel.HoldReason,
                    parcel.DueOn,
                    parcel.MeasuredWeightGrams ?? parcel.WeightGrams,
                    parcel.Issue,
                    parcel.IssueNote,
                    new DeliveryTags(
                        parcel.Speed!.IsDefault ? null : parcel.Speed.Name,
                        parcel.Speed.Icon,
                        parcel.ProductType!.IsDefault ? null : parcel.ProductType.Name,
                        parcel.ProductType.Icon,
                        parcel.ProductType.Handling,
                        parcel.Speed.GoesFirst || parcel.ProductType.GoesFirst))
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var hubNames = await db.Hubs.ToDictionaryAsync(h => h.Id, h => (h.Code, h.Name), cancellationToken);
        var rows = here
            .Select(p => new
            {
                p.CurrentHubId,
                p.Status,
                p.DeliveryHubId,
                p.PickupHubId,
                p.MerchantPhone,
                Row = p.Row with { HubCode = hubNames[p.CurrentHubId].Code }
            })
            .ToList();

        var toAssign = rows
            .Where(p => p.Status is ParcelStatus.AtHub or ParcelStatus.OnHold && p.DeliveryHubId == p.CurrentHubId)
            .Select(p => p.Row)
            .ToList();
        var toDispatch = rows
            .Where(p => (p.Status == ParcelStatus.AtHub && p.DeliveryHubId != p.CurrentHubId) ||
                (p.Status == ParcelStatus.Returning && p.PickupHubId != p.CurrentHubId))
            .GroupBy(p => (From: p.CurrentHubId, Returns: p.Status == ParcelStatus.Returning, To: p.Status == ParcelStatus.Returning ? p.PickupHubId : p.DeliveryHubId))
            .Select(g => new DispatchGroup(
                hubNames[g.Key.From].Code,
                hubNames[g.Key.To].Code,
                hubNames[g.Key.To].Name,
                g.Key.Returns,
                [.. g.Select(p => p.Row)]))
            .OrderBy(g => g.FromHubCode)
            .ThenBy(g => g.Returns)
            .ThenBy(g => g.HubName)
            .ToList();
        var toHandBack = rows
            .Where(p => p.Status == ParcelStatus.Returning && p.PickupHubId == p.CurrentHubId)
            .GroupBy(p => (p.CurrentHubId, p.Row.Merchant, p.MerchantPhone))
            .Select(g => new HandBackGroup(hubNames[g.Key.CurrentHubId].Code, g.Key.Merchant, g.Key.MerchantPhone, [.. g.Select(p => p.Row)]))
            .OrderBy(g => g.HubCode)
            .ThenBy(g => g.Merchant)
            .ToList();

        var tenant = tenantContext.Require();
        var now = time.GetUtcNow().UtcDateTime;
        var today = tenant.Today(now);
        var incoming = await db.Parcels.CountAsync(
            p => (p.TransferToHubId != null && (hubId == null || p.TransferToHubId == hubId)) ||
                (p.Status == ParcelStatus.PickedUp && (hubId == null || p.PickupHubId == hubId)),
            cancellationToken);
        // Today's pickups; tomorrow's list (parcels booked after the cut-off) is not work yet
        var pickups = await db.PickupRequests.CountAsync(
            r => (hubId == null || r.HubId == hubId) && r.PickupDate <= today &&
                (r.Status == PickupStatus.Scheduled || r.Status == PickupStatus.Assigned),
            cancellationToken);
        var openRuns = db.DeliveryRuns.Where(r => (hubId == null || r.HubId == hubId) && r.Status == RunStatus.Open);
        var withRiders = await (
            from attempt in db.DeliveryAttempts
            join run in openRuns on attempt.RunId equals run.Id
            where attempt.Outcome == null
            select attempt.Id)
            .CountAsync(cancellationToken);
        var cash = await (
            from attempt in db.DeliveryAttempts
            join run in openRuns on attempt.RunId equals run.Id
            select (decimal?)attempt.CollectedAmount)
            .SumAsync(cancellationToken) ?? 0;
        var runDates = await openRuns.Select(r => r.RunDate).ToListAsync(cancellationToken);

        return new HubBoard(
            hub,
            incoming,
            pickups,
            toAssign,
            toDispatch,
            toHandBack,
            withRiders,
            runDates.Count,
            runDates.Count(day => tenant.RunIsLate(day, now)),
            cash,
            today);
    }
}
