using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Domain.Common;
using Domain.Delivery;
using Domain.Network;
using Domain.Parcels;

namespace Application.Network.Coverage;

public sealed record HubRow(
    long Id,
    string Code,
    string Name,
    string Address,
    string Phone,
    bool Active,
    int Zones,
    int Riders,
    int Parcels);

public sealed record ZoneRow(
    long Id,
    string Code,
    string Name,
    string City,
    bool Suburb,
    long HubId,
    string Hub,
    bool Active,
    int Areas);

public sealed record AreaRow(long Id, string Name, long ZoneId, string Zone, string City, string Hub, bool Active, int Parcels);

public sealed record NewHub(string? Code, string? Name, string? Address, string? Phone);

public sealed record NewZone(string? Code, string? Name, long HubId, string? City, bool IsSuburb);

/// <summary>
/// The courier's own coverage map, kept by its admin: open and close hubs, draw zones on them with the city and
/// suburb flag that price a parcel, and keep the areas addresses are picked from. Nothing here is ever deleted — a
/// hub, zone or area that leaves the map is archived, so the parcels that used it keep their route and their charge.
/// </summary>
public class CoverageAdminHandler(IAppDbContext db)
{
    public static readonly Error HubNotFound = Error.NotFound("hub.notFound", "That hub was not found.");

    public static readonly Error ZoneNotFound = Error.NotFound("zone.notFound", "That zone was not found.");

    public static readonly Error AreaNotFound = Error.NotFound("area.notFound", "That area was not found.");

    private static readonly Error NoHub = Error.Validation("zone.hub", "Choose the hub that serves the zone.");

    private static readonly Error NoZone = Error.Validation("area.zone", "Choose the zone the area belongs to.");

    public async Task<IReadOnlyList<HubRow>> HubsAsync(CancellationToken cancellationToken = default)
    {
        return await db.Hubs
            .OrderBy(h => h.Archived)
            .ThenBy(h => h.Name)
            .Select(h => new HubRow(
                h.Id,
                h.Code,
                h.Name,
                h.Address,
                h.Phone,
                !h.Archived,
                db.Zones.Count(z => z.HubId == h.Id && !z.Archived),
                db.Riders.Count(r => r.HubId == h.Id && !r.Archived),
                db.Parcels.Count(p => !ParcelStatuses.Final.Contains(p.Status) &&
                    (p.CurrentHubId == h.Id || p.TransferToHubId == h.Id || (p.Status == ParcelStatus.Pending && p.PickupHubId == h.Id)))))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ZoneRow>> ZonesAsync(CancellationToken cancellationToken = default)
    {
        return await db.Zones
            .OrderBy(z => z.Archived)
            .ThenBy(z => z.City)
            .ThenBy(z => z.Name)
            .Select(z => new ZoneRow(
                z.Id,
                z.Code,
                z.Name,
                z.City,
                z.IsSuburb,
                z.HubId,
                z.Hub!.Name,
                !z.Archived,
                db.Areas.Count(a => a.ZoneId == z.Id && !a.Archived)))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AreaRow>> AreasAsync(long? zoneId, CancellationToken cancellationToken = default)
    {
        var areas = zoneId is { } only ? db.Areas.Where(a => a.ZoneId == only) : db.Areas;

        return await areas
            .OrderBy(a => a.Archived)
            .ThenBy(a => a.Zone!.City)
            .ThenBy(a => a.Name)
            .Select(a => new AreaRow(
                a.Id,
                a.Name,
                a.ZoneId,
                a.Zone!.Name,
                a.Zone.City,
                a.Zone.Hub!.Name,
                !a.Archived,
                db.Parcels.Count(p => p.AreaId == a.Id && !ParcelStatuses.Final.Contains(p.Status))))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<Result> AddHubAsync(NewHub spec, CancellationToken cancellationToken = default)
    {
        var hub = Hub.Create(spec.Code, spec.Name, spec.Address, spec.Phone);
        if (hub.IsFailure)
        {
            return hub.Error!;
        }

        if (await db.Hubs.AnyAsync(h => h.Code == hub.Value.Code, cancellationToken))
        {
            return TakenCode("hub.code", hub.Value.Code);
        }

        db.Hubs.Add(hub.Value);
        await db.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    public async Task<Result> EditHubAsync(long id, NewHub spec, CancellationToken cancellationToken = default)
    {
        var hub = await db.Hubs.SingleOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (hub is null)
        {
            return HubNotFound;
        }

        var code = Codes.Read(spec.Code, "hub.code", "Give the hub a short code, such as MIR: letters and digits, at most 20.");
        if (code.IsSuccess && code.Value != hub.Code && await db.Hubs.AnyAsync(h => h.Code == code.Value, cancellationToken))
        {
            return TakenCode("hub.code", code.Value);
        }

        var changed = hub.Change(spec.Code, spec.Name, spec.Address, spec.Phone);
        if (changed.IsSuccess)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }

    public async Task<Result> SetHubActiveAsync(long id, bool active, CancellationToken cancellationToken = default)
    {
        var hub = await db.Hubs.SingleOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (hub is null)
        {
            return HubNotFound;
        }

        if (active)
        {
            hub.Restore();
        }
        else
        {
            if (await HubBusyAsync(hub, cancellationToken) is { } busy)
            {
                return busy;
            }

            hub.Archive();
        }

        await db.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    public async Task<Result> AddZoneAsync(NewZone spec, CancellationToken cancellationToken = default)
    {
        if (!await db.Hubs.AnyAsync(h => h.Id == spec.HubId && !h.Archived, cancellationToken))
        {
            return NoHub;
        }

        var zone = Zone.Create(spec.Code, spec.Name, spec.HubId, spec.City, spec.IsSuburb);
        if (zone.IsFailure)
        {
            return zone.Error!;
        }

        if (await db.Zones.AnyAsync(z => z.Code == zone.Value.Code, cancellationToken))
        {
            return TakenCode("zone.code", zone.Value.Code);
        }

        db.Zones.Add(zone.Value);
        await db.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    public async Task<Result> EditZoneAsync(long id, NewZone spec, CancellationToken cancellationToken = default)
    {
        var zone = await db.Zones.SingleOrDefaultAsync(z => z.Id == id, cancellationToken);
        if (zone is null)
        {
            return ZoneNotFound;
        }

        if (!await db.Hubs.AnyAsync(h => h.Id == spec.HubId && !h.Archived, cancellationToken))
        {
            return NoHub;
        }

        var code = Codes.Read(spec.Code, "zone.code", "Give the zone a short code, such as MIR: letters and digits, at most 20.");
        if (code.IsSuccess && code.Value != zone.Code && await db.Zones.AnyAsync(z => z.Code == code.Value, cancellationToken))
        {
            return TakenCode("zone.code", code.Value);
        }

        var reroutes = spec.HubId != zone.HubId || spec.IsSuburb != zone.IsSuburb ||
            !string.Equals(spec.City.NullIfBlank(), zone.City, StringComparison.OrdinalIgnoreCase);
        if (reroutes && await ZoneBusyAsync(zone, cancellationToken) is { } busy)
        {
            return busy;
        }

        var changed = zone.Change(spec.Code, spec.Name, spec.HubId, spec.City, spec.IsSuburb);
        if (changed.IsSuccess)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }

    public async Task<Result> SetZoneActiveAsync(long id, bool active, CancellationToken cancellationToken = default)
    {
        var zone = await db.Zones.SingleOrDefaultAsync(z => z.Id == id, cancellationToken);
        if (zone is null)
        {
            return ZoneNotFound;
        }

        if (active)
        {
            if (await db.Hubs.AnyAsync(h => h.Id == zone.HubId && h.Archived, cancellationToken))
            {
                return Error.Conflict(
                    "zone.hubClosed",
                    $"{zone.Name} is served by a hub that is closed. Open the hub again, or move the zone to another one, first.");
            }

            zone.Restore();
        }
        else
        {
            var areas = await db.Areas.CountAsync(a => a.ZoneId == zone.Id && !a.Archived, cancellationToken);
            if (areas > 0)
            {
                return Error.Conflict(
                    "zone.hasAreas",
                    $"{zone.Name} still covers {areas} area{(areas == 1 ? "" : "s")}. Take those off the map first, so no " +
                    "address can be booked to a zone the courier no longer covers.");
            }

            zone.Archive();
        }

        await db.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    public async Task<Result> AddAreaAsync(string? name, long zoneId, CancellationToken cancellationToken = default)
    {
        if (!await db.Zones.AnyAsync(z => z.Id == zoneId && !z.Archived, cancellationToken))
        {
            return NoZone;
        }

        var area = Area.Create(name, zoneId);
        if (area.IsFailure)
        {
            return area.Error!;
        }

        if (await db.Areas.AnyAsync(a => a.Name == area.Value.Name, cancellationToken))
        {
            return TakenArea(area.Value.Name);
        }

        db.Areas.Add(area.Value);
        await db.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    public async Task<Result> EditAreaAsync(long id, string? name, long zoneId, CancellationToken cancellationToken = default)
    {
        var area = await db.Areas.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (area is null)
        {
            return AreaNotFound;
        }

        if (!await db.Zones.AnyAsync(z => z.Id == zoneId && !z.Archived, cancellationToken))
        {
            return NoZone;
        }

        var trimmed = name.NullIfBlank();
        if (trimmed is not null && trimmed != area.Name && await db.Areas.AnyAsync(a => a.Name == trimmed, cancellationToken))
        {
            return TakenArea(trimmed);
        }

        if (zoneId != area.ZoneId && await AreaBusyAsync(area, "moved to another zone", cancellationToken) is { } busy)
        {
            return busy;
        }

        var changed = area.Change(name, zoneId);
        if (changed.IsSuccess)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }

    public async Task<Result> SetAreaActiveAsync(long id, bool active, CancellationToken cancellationToken = default)
    {
        var area = await db.Areas.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (area is null)
        {
            return AreaNotFound;
        }

        if (active)
        {
            if (await db.Zones.AnyAsync(z => z.Id == area.ZoneId && z.Archived, cancellationToken))
            {
                return Error.Conflict(
                    "area.zoneClosed",
                    $"{area.Name} sits in a zone the courier no longer covers. Put the zone back on the map, or move the " +
                    "area to another one, first.");
            }

            area.Restore();
        }
        else
        {
            if (await AreaBusyAsync(area, "taken off the map", cancellationToken) is { } busy)
            {
                return busy;
            }

            area.Archive();
        }

        await db.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    private static Error TakenCode(string code, string value)
    {
        return Error.Conflict(code, $"The code {value} is already used. Every hub and every zone needs a code of its own.");
    }

    private static Error TakenArea(string name)
    {
        return Error.Conflict("area.name", $"\"{name}\" is already on the map. An area is named once for the whole courier.");
    }

    /// <summary>
    /// Why the hub cannot close yet: its zones would route parcels to a hub that is shut, its riders and staff would sign
    /// in to no work, and the parcels standing on its shelves would have nowhere to be scanned.
    /// </summary>
    private async Task<Error?> HubBusyAsync(Hub hub, CancellationToken cancellationToken)
    {
        var zones = await db.Zones.CountAsync(z => z.HubId == hub.Id && !z.Archived, cancellationToken);
        var riders = await db.Riders.CountAsync(r => r.HubId == hub.Id && !r.Archived, cancellationToken);
        var staff = await db.StaffMembers.CountAsync(s => s.HubId == hub.Id, cancellationToken);
        var parcels = await db.Parcels.CountAsync(
            p => !ParcelStatuses.Final.Contains(p.Status) &&
                (p.CurrentHubId == hub.Id || p.TransferToHubId == hub.Id || (p.Status == ParcelStatus.Pending && p.PickupHubId == hub.Id)),
            cancellationToken);
        var pickups = await db.PickupRequests.CountAsync(
            r => r.HubId == hub.Id && (r.Status == PickupStatus.Scheduled || r.Status == PickupStatus.Assigned),
            cancellationToken);
        if (zones + riders + staff + parcels + pickups == 0)
        {
            return null;
        }

        List<string> open = [];
        if (zones > 0)
        {
            open.Add($"{zones} zone{(zones == 1 ? "" : "s")} it serves");
        }

        if (riders > 0)
        {
            open.Add($"{riders} rider{(riders == 1 ? "" : "s")} working from it");
        }

        if (staff > 0)
        {
            open.Add($"{staff} staff login{(staff == 1 ? "" : "s")} working there");
        }

        if (parcels > 0)
        {
            open.Add($"{parcels} parcel{(parcels == 1 ? "" : "s")} there or on the way to it");
        }

        if (pickups > 0)
        {
            open.Add($"{pickups} pickup{(pickups == 1 ? "" : "s")} still to make");
        }

        return Error.Conflict(
            "hub.busy",
            $"{hub.Name} cannot close yet: {string.Join(", ", open)}. Move its zones to another hub, move or stop its " +
            "riders, move its staff to another hub, and clear its shelves first.");
    }

    /// <summary>
    /// Why the zone cannot change hub, city or suburb flag yet: the parcels on their way were priced by this city and
    /// flag and routed to this hub, so changing it now would charge and route them from somewhere else halfway.
    /// </summary>
    private async Task<Error?> ZoneBusyAsync(Zone zone, CancellationToken cancellationToken)
    {
        var parcels = await db.Parcels.CountAsync(
            p => !ParcelStatuses.Final.Contains(p.Status) &&
                (db.Areas.Any(a => a.Id == p.AreaId && a.ZoneId == zone.Id) ||
                    db.PickupPoints.Any(point => point.Id == p.PickupPointId &&
                        db.Areas.Any(a => a.Id == point.AreaId && a.ZoneId == zone.Id))),
            cancellationToken);
        if (parcels == 0)
        {
            return null;
        }

        return Error.Conflict(
            "zone.busy",
            $"{zone.Name} cannot change hub, city or suburb yet: {parcels} parcel{(parcels == 1 ? " was" : "s were")} " +
            "priced and routed by the zone as it stands. Finish them first, or make a new zone and move the areas across.");
    }

    /// <summary>Why the area cannot move or leave the map: the parcels going there were priced and routed by it.</summary>
    private async Task<Error?> AreaBusyAsync(Area area, string what, CancellationToken cancellationToken)
    {
        var parcels = await db.Parcels.CountAsync(
            p => p.AreaId == area.Id && !ParcelStatuses.Final.Contains(p.Status),
            cancellationToken);
        var points = await db.PickupPoints.CountAsync(point => point.AreaId == area.Id && !point.Archived, cancellationToken);
        if (parcels + points == 0)
        {
            return null;
        }

        List<string> open = [];
        if (parcels > 0)
        {
            open.Add($"{parcels} parcel{(parcels == 1 ? "" : "s")} on the way there");
        }

        if (points > 0)
        {
            open.Add($"{points} merchant pickup point{(points == 1 ? "" : "s")} in it");
        }

        return Error.Conflict(
            "area.busy",
            $"{area.Name} cannot be {what} yet: {string.Join(", ", open)}. Those were priced and routed by the area as " +
            "it stands.");
    }
}
