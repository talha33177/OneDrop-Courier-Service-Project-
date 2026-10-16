using Domain.Common;

namespace Domain.Network;

/// <summary>
/// A part of the coverage map served by one hub: a city area (Mirpur), a suburb of a city (Savar) or a district
/// (Sylhet). <see cref="City"/> and <see cref="IsSuburb"/> decide the service area a parcel is charged at
/// (<see cref="Pricing.ServiceAreas.Between"/>). A hub can serve several zones. A zone the courier stops covering is
/// archived, never deleted: the parcels priced by it keep their charges.
/// </summary>
public class Zone : TenantEntity, IArchivable
{
    private Zone()
    {
    }

    public string Code { get; private set; } = "";

    public string Name { get; private set; } = "";

    public long HubId { get; private set; }

    public Hub? Hub { get; private set; }

    /// <summary>The city or district the zone belongs to, e.g. "Dhaka" for both Mirpur and Savar.</summary>
    public string City { get; private set; } = "";

    /// <summary>A suburb of <see cref="City"/> (Savar, Gazipur), charged between the city and outside-city rates.</summary>
    public bool IsSuburb { get; private set; }

    /// <summary>The division the zone is in (Dhaka, Chattogram…): where the address picker files it. Null: not filed.</summary>
    public string? Division { get; private set; }

    public bool Archived { get; private set; }

    public static Result<Zone> Create(string? code, string? name, long hubId, string? city, bool isSuburb, string? division = null)
    {
        var zone = new Zone();
        var changed = zone.Change(code, name, hubId, city, isSuburb, division);

        return changed.IsSuccess ? zone : changed.Error!;
    }

    public Result Change(string? code, string? name, long hubId, string? city, bool isSuburb, string? division = null)
    {
        var knownDivision = Divisions.Find(division);
        if (division.NullIfBlank() is not null && knownDivision is null)
        {
            return Error.Validation("zone.division", "Choose one of Bangladesh's eight divisions.");
        }

        var shortCode = Codes.Read(code, "zone.code", "Give the zone a short code, such as MIR: letters and digits, at most 20.");
        if (shortCode.IsFailure)
        {
            return shortCode.Error!;
        }

        var trimmed = name.NullIfBlank();
        if (trimmed is null || trimmed.Length > 200)
        {
            return Error.Validation("zone.name", "Name the zone, such as \"Mirpur\", at most 200 characters.");
        }

        var town = city.NullIfBlank();
        if (town is null || town.Length > 60)
        {
            return Error.Validation("zone.city", "Name the city or district the zone belongs to, at most 60 characters.");
        }

        Code = shortCode.Value;
        Name = trimmed;
        HubId = hubId;
        City = town;
        IsSuburb = isSuburb;
        Division = knownDivision;

        return Result.Success();
    }

    /// <summary>The courier stops covering the zone: nothing new is booked to its areas, its history stays.</summary>
    public void Archive()
    {
        Archived = true;
    }

    public void Restore()
    {
        Archived = false;
    }
}
