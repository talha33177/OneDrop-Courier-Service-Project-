using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Application.Platform.Staff;
using Domain.Delivery;
using Domain.Parcels;
using Domain.Payments;

namespace Application.Hubs.HubOverview;

/// <summary>
/// What waits at one hub, so staff can see where work is before they pick a hub. <c>ToAssign</c> is the parcels a rider can
/// be given today: a parcel the customer asked to have on a later day is not work yet.
/// </summary>
public sealed record HubWork(HubItem Hub, int PickupsOpen, int ToAssign, int Incoming, int WithRiders, int OpenRuns)
{
    /// <summary>
    /// Work waiting: a run whose parcels are all recorded still waits to be closed with its cash, so it counts when no
    /// parcel is out.
    /// </summary>
    public int Total => PickupsOpen + ToAssign + Incoming + WithRiders + (WithRiders == 0 ? OpenRuns : 0);

    /// <summary>Cash riders handed in at closing that the hub has not paid into the bank yet.</summary>
    public decimal CashAtHub { get; init; }

    /// <summary>Runs closed short whose shortfall the hub manager has not accepted.</summary>
    public int Shortfalls { get; init; }

    /// <summary>Money to pay in, or a shortfall to accept: what the Cash page is for.</summary>
    public bool CashWaiting => CashAtHub > 0 || Shortfalls > 0;
}

/// <summary>Counts every hub's open work from the pickups, parcels and runs when asked: nothing is stored.</summary>
public class HubOverviewHandler(
    IAppDbContext db,
    HubDirectory hubs,
    StaffAccess staff,
    ITenantContext tenantContext,
    TimeProvider time)
{
    /// <summary>
    /// The hubs the signed-in person works at, in name order, with their open work (today's pickups, not tomorrow's
    /// list): every hub of the courier for head office, only their own for a hub's staff. Every hub page finds its hub
    /// in this list, so a hub's staff cannot open another hub's page whatever code they send.
    /// </summary>
    public async Task<IReadOnlyList<HubWork>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rights = await staff.CurrentAsync(cancellationToken);
        var all = (await hubs.ListAsync(cancellationToken)).Where(hub => rights.WorksAt(hub.Id)).ToList();
        var today = tenantContext.Require().Today(time.GetUtcNow().UtcDateTime);
        var pickups = await db.PickupRequests
            .Where(r => r.PickupDate <= today && (r.Status == PickupStatus.Scheduled || r.Status == PickupStatus.Assigned))
            .GroupBy(r => r.HubId)
            .Select(g => new { HubId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.HubId, x => x.Count, cancellationToken);
        var toAssign = await db.Parcels
            .Where(p => (p.Status == ParcelStatus.AtHub || p.Status == ParcelStatus.OnHold) && p.CurrentHubId == p.DeliveryHubId &&
                (p.HoldUntil == null || p.HoldUntil <= today))
            .GroupBy(p => p.DeliveryHubId)
            .Select(g => new { HubId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.HubId, x => x.Count, cancellationToken);
        var incoming = await db.Parcels
            .Where(p => p.TransferToHubId != null || p.Status == ParcelStatus.PickedUp)
            .GroupBy(p => p.TransferToHubId ?? p.PickupHubId)
            .Select(g => new { HubId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.HubId, x => x.Count, cancellationToken);
        var withRiders = await (
            from attempt in db.DeliveryAttempts
            join run in db.DeliveryRuns on attempt.RunId equals run.Id
            where attempt.Outcome == null && run.Status == RunStatus.Open
            group attempt by run.HubId into g
            select new { HubId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.HubId, x => x.Count, cancellationToken);
        var openRuns = await db.DeliveryRuns
            .Where(r => r.Status == RunStatus.Open)
            .GroupBy(r => r.HubId)
            .Select(g => new { HubId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.HubId, x => x.Count, cancellationToken);
        var handedIn = await db.DeliveryRuns
            .Where(r => r.Status == RunStatus.Closed)
            .GroupBy(r => r.HubId)
            .Select(g => new { HubId = g.Key, Amount = g.Sum(r => r.CashReceived) ?? 0 })
            .ToDictionaryAsync(x => x.HubId, x => x.Amount, cancellationToken);
        var deposited = await db.CashDeposits
            .Where(d => d.Status != CashDepositStatus.Refused)
            .GroupBy(d => d.HubId)
            .Select(g => new { HubId = g.Key, Amount = g.Sum(d => d.Amount) })
            .ToDictionaryAsync(x => x.HubId, x => x.Amount, cancellationToken);
        var shortfalls = await db.DeliveryRuns
            .Where(r => r.Status == RunStatus.Closed && r.CashReceived < r.CashExpected && r.ShortAcceptedOn == null)
            .GroupBy(r => r.HubId)
            .Select(g => new { HubId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.HubId, x => x.Count, cancellationToken);

        return
        [
            .. all.Select(hub => new HubWork(
                hub,
                pickups.GetValueOrDefault(hub.Id),
                toAssign.GetValueOrDefault(hub.Id),
                incoming.GetValueOrDefault(hub.Id),
                withRiders.GetValueOrDefault(hub.Id),
                openRuns.GetValueOrDefault(hub.Id))
            {
                CashAtHub = handedIn.GetValueOrDefault(hub.Id) - deposited.GetValueOrDefault(hub.Id),
                Shortfalls = shortfalls.GetValueOrDefault(hub.Id)
            })
        ];
    }
}
