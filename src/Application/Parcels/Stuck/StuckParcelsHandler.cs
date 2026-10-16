using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Domain.Parcels;

namespace Application.Parcels.Stuck;

/// <summary>A parcel that has stayed where it is for longer than the courier allows, and where that is.</summary>
public sealed record StuckParcel(
    string TrackingCode,
    string Merchant,
    ParcelStatus Status,
    string Where,
    string? HubCode,
    DateTime MovedOn,
    int Days,
    DateOnly? HoldUntil,
    ParcelIssue? Issue);

public sealed record StuckParcels(int? AfterDays, IReadOnlyList<StuckParcel> Parcels, IReadOnlyDictionary<ParcelStatus, int> ByStatus)
{
    public int Count => ByStatus.Values.Sum();
}

/// <summary>
/// Parcels that have not moved for the courier's <c>StuckAfterDays</c>: still with the rider who picked them up, at a
/// hub, between hubs, going back to the merchant, or on hold that many days past the day they were held for. A parcel
/// moves when its status or its place changes (<c>Parcel.MovedOn</c>); a note in its history does not count. Out for
/// delivery is not watched: a run is closed every day. A courier with no days set watches for none.
/// </summary>
public class StuckParcelsHandler(IAppDbContext db, ITenantContext tenantContext, TimeProvider time)
{
    /// <summary>The most parcels listed at once, the longest stuck first.</summary>
    public const int MaxListed = 200;

    public static readonly ParcelStatus[] Watched =
        [ParcelStatus.PickedUp, ParcelStatus.AtHub, ParcelStatus.InTransit, ParcelStatus.OnHold, ParcelStatus.Returning];

    /// <summary>The parcels stuck as of <paramref name="utcNow"/>; none when the courier watches for none.</summary>
    public static IQueryable<Parcel> Stuck(IQueryable<Parcel> parcels, TenantInfo tenant, DateTime utcNow)
    {
        if (tenant.StuckAfterDays is not { } days)
        {
            return parcels.Where(p => false);
        }

        var before = utcNow.AddDays(-days);
        var heldBefore = tenant.Today(utcNow).AddDays(-days);

        return parcels.Where(p =>
            Watched.Contains(p.Status) &&
            p.MovedOn < before &&
            (p.Status != ParcelStatus.OnHold || p.HoldUntil == null || p.HoldUntil <= heldBefore));
    }

    public async Task<StuckParcels> ListAsync(ParcelStatus? status, CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var now = time.GetUtcNow().UtcDateTime;
        var stuck = Stuck(db.Parcels, tenant, now);
        var byStatus = await stuck
            .GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Status, g => g.Count, cancellationToken);
        var rows = await stuck
            .Where(p => status == null || p.Status == status)
            .OrderBy(p => p.MovedOn)
            .Take(MaxListed)
            .Select(p => new
            {
                p.TrackingCode,
                Merchant = db.Merchants.Where(m => m.Id == p.MerchantId).Select(m => m.Name).First(),
                p.Status,
                At = db.Hubs.Where(h => h.Id == p.CurrentHubId).Select(h => new { h.Code, h.Name }).FirstOrDefault(),
                To = db.Hubs.Where(h => h.Id == p.TransferToHubId).Select(h => h.Name).FirstOrDefault(),
                Rider = db.Riders.Where(r => r.Id == p.RiderId).Select(r => r.Name).FirstOrDefault(),
                // A parcel just picked up is on no shelf and with no rider in the books: it is on its way to its pickup hub
                PickupHub = db.Hubs.Where(h => h.Id == p.PickupHubId).Select(h => h.Name).First(),
                p.MovedOn,
                p.HoldUntil,
                p.Issue
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return new StuckParcels(
            tenant.StuckAfterDays,
            [
                .. rows.Select(r => new StuckParcel(
                    r.TrackingCode,
                    r.Merchant,
                    r.Status,
                    r.At is { } hub ? hub.Name : r.To is { } to ? $"On the way to {to}" : r.Rider is { } rider ? $"With {rider}" : $"Picked up, for {r.PickupHub}",
                    r.At?.Code,
                    tenant.Local(r.MovedOn),
                    (int)(now - r.MovedOn).TotalDays,
                    r.HoldUntil,
                    r.Issue))
            ],
            byStatus);
    }
}
