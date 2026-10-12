using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Domain.Delivery;
using Domain.Parcels;
using Domain.Pricing;

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
    int WeightGrams,
    ParcelIssue? Issue,
    string? IssueNote,
    DeliverySpeed Speed,
    ProductType ProductType)
{
    public bool IsPriority => DeliveryOptions.IsPriority(Speed, ProductType);
}

/// <summary>Parcels at the hub that leave for one other hub: forward ones, or returns going back.</summary>
public sealed record DispatchGroup(string HubCode, string HubName, bool Returns, IReadOnlyList<BoardParcel> Parcels);

/// <summary>Returning parcels at the hub that go back to one merchant.</summary>
public sealed record HandBackGroup(string Merchant, string MerchantPhone, IReadOnlyList<BoardParcel> Parcels);

/// <summary>
/// The hub's day at a glance: what is coming in, what waits for a rider (the most urgent first) and how much of it is
/// late, what to send to other hubs, what to hand back to merchants, what the riders have out and how many of them are
/// late back.
/// </summary>
public sealed record HubBoard(
    HubItem Hub,
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

/// <summary>Works out a hub's board from the parcels, pickups and runs when asked: nothing is stored.</summary>
public class HubBoardHandler(IAppDbContext db, ITenantContext tenantContext, HubDirectory hubs, TimeProvider time)
{
    public async Task<HubBoard?> GetAsync(string hubCode, CancellationToken cancellationToken = default)
    {
        var hub = await hubs.FindAsync(hubCode, cancellationToken);
        if (hub is null)
        {
            return null;
        }

        var here = await (
            from parcel in db.Parcels
            join merchant in db.Merchants on parcel.MerchantId equals merchant.Id
            join area in db.Areas on parcel.AreaId equals area.Id
            where parcel.CurrentHubId == hub.Id
            orderby parcel.Speed == DeliverySpeed.Express || parcel.ProductType == ProductType.FreshFood ||
                parcel.ProductType == ProductType.Frozen descending, parcel.DueOn == null, parcel.DueOn, parcel.Id
            select new
            {
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
                    parcel.WeightGrams,
                    parcel.Issue,
                    parcel.IssueNote,
                    parcel.Speed,
                    parcel.ProductType)
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var hubNames = await db.Hubs.ToDictionaryAsync(h => h.Id, h => (h.Code, h.Name), cancellationToken);

        var toAssign = here
            .Where(p => p.Status is ParcelStatus.AtHub or ParcelStatus.OnHold && p.DeliveryHubId == hub.Id)
            .Select(p => p.Row)
            .ToList();
        var toDispatch = here
            .Where(p => (p.Status == ParcelStatus.AtHub && p.DeliveryHubId != hub.Id) ||
                (p.Status == ParcelStatus.Returning && p.PickupHubId != hub.Id))
            .GroupBy(p => (Returns: p.Status == ParcelStatus.Returning, HubId: p.Status == ParcelStatus.Returning ? p.PickupHubId : p.DeliveryHubId))
            .Select(g => new DispatchGroup(hubNames[g.Key.HubId].Code, hubNames[g.Key.HubId].Name, g.Key.Returns, [.. g.Select(p => p.Row)]))
            .OrderBy(g => g.Returns)
            .ThenBy(g => g.HubName)
            .ToList();
        var toHandBack = here
            .Where(p => p.Status == ParcelStatus.Returning && p.PickupHubId == hub.Id)
            .GroupBy(p => (p.Row.Merchant, p.MerchantPhone))
            .Select(g => new HandBackGroup(g.Key.Merchant, g.Key.MerchantPhone, [.. g.Select(p => p.Row)]))
            .OrderBy(g => g.Merchant)
            .ToList();

        var tenant = tenantContext.Require();
        var now = time.GetUtcNow().UtcDateTime;
        var today = tenant.Today(now);
        var incoming = await db.Parcels.CountAsync(p => p.TransferToHubId == hub.Id, cancellationToken);
        // Today's pickups; tomorrow's list (parcels booked after the cut-off) is not work yet
        var pickups = await db.PickupRequests.CountAsync(
            r => r.HubId == hub.Id && r.PickupDate <= today &&
                (r.Status == PickupStatus.Scheduled || r.Status == PickupStatus.Assigned),
            cancellationToken);
        var openRuns = db.DeliveryRuns.Where(r => r.HubId == hub.Id && r.Status == RunStatus.Open);
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
            new HubItem(hub.Id, hub.Code, hub.Name, hub.Address, hub.Phone),
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
