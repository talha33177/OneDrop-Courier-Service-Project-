using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Domain.Common;
using Domain.Staff;

namespace Application.Platform.Staff;

/// <summary>
/// What the signed-in member of the courier's staff may do, and where. A super admin may do everything everywhere; a
/// staff login may do what its row says, at its hub or, from head office, at every hub. A staff login with no row may
/// do nothing, so a login made by hand is shut until someone gives it a job.
/// </summary>
public sealed record StaffRights(
    bool IsSuperAdmin,
    bool HasJob,
    StaffJob? Job,
    StaffPermissions Permissions,
    long? HubId,
    string? HubCode,
    string? HubName)
{
    public static readonly StaffRights SuperAdmin = new(true, true, null, StaffPermissions.All, null, null, null);

    public static readonly StaffRights Nobody = new(false, false, null, StaffPermissions.None, null, null, null);

    /// <summary>Head office works at every hub; everyone else at their own.</summary>
    public bool AtHeadOffice => HubId is null;

    /// <summary>Whether they hold any of <paramref name="any"/>; nothing asked needs only a job.</summary>
    public bool May(StaffPermissions any)
    {
        return IsSuperAdmin || (HasJob && (any == StaffPermissions.None || (Permissions & any) != 0));
    }

    /// <summary>Whether they hold every one of <paramref name="all"/>: nobody hands out what they do not hold.</summary>
    public bool Holds(StaffPermissions all)
    {
        return IsSuperAdmin || (HasJob && (Permissions & all) == all);
    }

    /// <summary>Whether they may act at <paramref name="hubId"/>: their own hub, or any from head office.</summary>
    public bool WorksAt(long? hubId)
    {
        return IsSuperAdmin || (HasJob && (AtHeadOffice || HubId == hubId));
    }

    /// <summary>"Receiving at Mirpur hub", "Accounts", "Super admin": who they are, for the account card.</summary>
    public string Title => IsSuperAdmin ? "Super admin"
        : Job is not { } job ? "No job yet"
        : HubName is null ? job.DisplayName()
        : $"{job.DisplayName()} · {HubName}";

    public static readonly Error NotAtYourHub = Error.Validation(
        "staff.notYourHub",
        "This parcel is not at your hub. The hub that has it does this.");
}

/// <summary>Reads the signed-in login's staff rights. Scoped, so the row is read once per request.</summary>
public class StaffAccess(IAppDbContext db, ICurrentUser currentUser)
{
    private StaffRights? rights;

    public async Task<StaffRights> CurrentAsync(CancellationToken cancellationToken = default)
    {
        if (rights is not null)
        {
            return rights;
        }

        if (currentUser.IsInRole(Roles.TenantAdmin))
        {
            return rights = StaffRights.SuperAdmin;
        }

        if (currentUser.UserId is not { } userId || !currentUser.IsInRole(Roles.HubStaff))
        {
            return rights = StaffRights.Nobody;
        }

        var member = await (
            from staff in db.StaffMembers.AsNoTracking()
            where staff.UserId == userId
            join hub in db.Hubs on staff.HubId equals hub.Id into hubs
            from hub in hubs.DefaultIfEmpty()
            select new StaffRights(false, true, staff.Job, staff.Permissions, staff.HubId, hub.Code, hub.Name))
            .SingleOrDefaultAsync(cancellationToken);

        return rights = member ?? StaffRights.Nobody;
    }
}
