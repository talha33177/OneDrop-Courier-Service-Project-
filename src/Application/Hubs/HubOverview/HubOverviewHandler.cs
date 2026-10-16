using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Application.Platform.Staff;
using Domain.Delivery;
using Domain.Parcels;

namespace Application.Hubs.HubOverview;

/// <summary>What waits at one hub, so staff can see where work is before they pick a hub.</summary>
public sealed record HubWork(HubItem Hub, int PickupsOpen, int ToAssign, int Incoming, int WithRiders, int OpenRuns)
{
    public int Total => PickupsOpen + ToAssign + Incoming + WithRiders;
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
            .Where(p => (p.Status == ParcelStatus.AtHub || p.Status == ParcelStatus.OnHold) && p.CurrentHubId == p.DeliveryHubId)
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

        return
        [
            .. all.Select(hub => new HubWork(
                hub,
                pickups.GetValueOrDefault(hub.Id),
                toAssign.GetValueOrDefault(hub.Id),
                incoming.GetValueOrDefault(hub.Id),
                withRiders.GetValueOrDefault(hub.Id),
                openRuns.GetValueOrDefault(hub.Id)))
        ];
    }
}
