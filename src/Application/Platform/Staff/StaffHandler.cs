using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Application.Notifications;
using Domain.Common;
using Domain.Staff;

namespace Application.Platform.Staff;

/// <summary>
/// Someone to add to the courier's own people: their name, the email they sign in with, their job (a
/// <see cref="StaffJob"/> name, or <see cref="StaffHandler.SuperAdmin"/>), the hub a hub job is worked at, and the
/// permissions ticked on top of the job's own.
/// </summary>
public sealed record NewStaff(string? Name, string? Email, string? Job, long? HubId, StaffPermissions Extra);

/// <summary>One of the courier's own people with a sign-in, as the staff page shows them.</summary>
public sealed record StaffPerson(
    long UserId,
    string Name,
    string Email,
    bool IsSuperAdmin,
    StaffJob? Job,
    StaffPermissions Permissions,
    long? HubId,
    string? HubName,
    bool Stopped,
    DateTime Created)
{
    public string JobName => IsSuperAdmin ? "Super admin" : Job?.DisplayName() ?? "No job yet";

    public string Where => IsSuperAdmin || HubId is null ? "Head office" : HubName ?? "A closed hub";

    /// <summary>What was ticked on top of the job's own permissions.</summary>
    public StaffPermissions Extra => Job is { } job ? Permissions & ~job.Preset() : Permissions;
}

/// <summary>
/// The courier's own people with a sign-in: super admins and staff with a job at one hub or at head office. Riders have
/// their page and merchants their moderators. We send no email yet, so adding someone returns a first password to hand
/// over, as for moderators; the person changes it at /Account/Password.
/// Who manages whom: a super admin everyone; head office staff with the staff permission everyone below super admin;
/// a hub manager the staff of their own hub. Nobody hands out a permission they do not hold, or changes someone who
/// may do more than they may, and only a super admin makes super admins. Two guards keep the courier from locking
/// itself out: nobody stops or demotes themselves, and the last super admin who can sign in stays one.
/// </summary>
public class StaffHandler(
    IUserAccounts accounts,
    IAppDbContext db,
    ICurrentUser currentUser,
    ITenantContext tenantContext,
    StaffAccess access)
{
    /// <summary>The job value that makes a super admin rather than a staff member.</summary>
    public const string SuperAdmin = "SuperAdmin";

    public const int MaxNameLength = 200;

    public static readonly Error NotFound = Error.NotFound("staff.notFound", "That person was not found.");

    public static readonly Error NotYourself = Error.Validation(
        "staff.yourself",
        "You cannot stop yourself or change your own job. Ask another admin.");

    public static readonly Error LastAdmin = Error.Validation(
        "staff.lastAdmin",
        "This is the courier's last super admin who can sign in. Make someone else a super admin first.");

    public static readonly Error Above = Error.Validation(
        "staff.above",
        "This person may do more than you may, so only someone above you can change them.");

    /// <summary>Everyone the signed-in person may see on the staff page: super admins first, then by hub and name.</summary>
    public async Task<IReadOnlyList<StaffPerson>> ListAsync(CancellationToken cancellationToken = default)
    {
        var me = await access.CurrentAsync(cancellationToken);

        return
        [
            .. (await AllAsync(cancellationToken))
                .Where(person => MaySee(me, person))
                .OrderBy(person => person.Stopped)
                .ThenBy(person => !person.IsSuperAdmin)
                .ThenBy(person => person.HubId is not null)
                .ThenBy(person => person.HubName)
                .ThenBy(person => person.Name)
        ];
    }

    /// <summary>Whether <paramref name="me"/> may see <paramref name="person"/>: a hub manager sees their own hub only.</summary>
    public static bool MaySee(StaffRights me, StaffPerson person)
    {
        return me.IsSuperAdmin ||
            (me.AtHeadOffice ? me.May(StaffPermissions.Staff) : !person.IsSuperAdmin && person.HubId == me.HubId);
    }

    /// <summary>Whether <paramref name="me"/> may change, stop or give a new password to <paramref name="person"/>.</summary>
    public static bool MayManage(StaffRights me, StaffPerson person)
    {
        return MaySee(me, person) && (me.IsSuperAdmin || (!person.IsSuperAdmin && me.Holds(person.Permissions)));
    }

    /// <summary>Adds a login. The password in the result is shown once, to hand over.</summary>
    public async Task<Result<string>> AddAsync(NewStaff spec, CancellationToken cancellationToken = default)
    {
        var me = await access.CurrentAsync(cancellationToken);
        var details = await ReadAsync(me, spec.Name, spec.Job, spec.HubId, spec.Extra, cancellationToken);
        if (details.IsFailure)
        {
            return details.Error!;
        }

        var (name, job, hubId) = details.Value;
        var password = Passwords.New();
        var login = await accounts.CreateAsync(
            new NewLogin(spec.Email, password, name, job is null ? Roles.TenantAdmin : Roles.HubStaff),
            cancellationToken);
        if (login.IsFailure)
        {
            return login.Error!;
        }

        if (job is not null)
        {
            db.StaffMembers.Add(StaffMember.Add(login.Value, hubId, job.Value, spec.Extra).Value);
        }

        db.OutboxMessages.Add(OutboxContracts.Write(new AccountEmailMessage(login.Value, AccountEmailKind.LoginCreated)));
        await db.SaveChangesAsync(cancellationToken);

        return password;
    }

    /// <summary>A new name, job, hub or ticks. Moving someone in or out of super admin changes their role.</summary>
    public async Task<Result> ChangeAsync(
        long userId,
        string? name,
        string? job,
        long? hubId,
        StaffPermissions extra,
        CancellationToken cancellationToken = default)
    {
        var me = await access.CurrentAsync(cancellationToken);
        var person = await FindAsync(me, userId, cancellationToken);
        if (person.IsFailure)
        {
            return person.Error!;
        }

        var details = await ReadAsync(me, name, job, hubId, extra, cancellationToken);
        if (details.IsFailure)
        {
            return details.Error!;
        }

        var (newName, newJob, newHubId) = details.Value;
        var leavesAdmins = person.Value.Person.IsSuperAdmin && newJob is not null;
        if (leavesAdmins)
        {
            var allowed = MayLeaveAdmins(person.Value);
            if (allowed.IsFailure)
            {
                return allowed;
            }
        }

        var from = person.Value.Person.IsSuperAdmin ? Roles.TenantAdmin : Roles.HubStaff;
        var to = newJob is null ? Roles.TenantAdmin : Roles.HubStaff;
        var changed = await accounts.ChangeAsync(userId, newName, from, to, cancellationToken);
        if (changed.IsFailure || newJob is null)
        {
            return changed;
        }

        var row = await db.StaffMembers.SingleOrDefaultAsync(s => s.UserId == userId, cancellationToken);
        if (row is null)
        {
            db.StaffMembers.Add(StaffMember.Add(userId, newHubId, newJob.Value, extra).Value);
        }
        else
        {
            row.Change(newHubId, newJob.Value, extra);
        }

        await db.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    /// <summary>Stops someone: their login is shut out at once and any open session ends.</summary>
    public async Task<Result> StopAsync(long userId, CancellationToken cancellationToken = default)
    {
        var me = await access.CurrentAsync(cancellationToken);
        var person = await FindAsync(me, userId, cancellationToken);
        if (person.IsFailure)
        {
            return person.Error!;
        }

        if (person.Value.Person.IsSuperAdmin)
        {
            var allowed = MayLeaveAdmins(person.Value);
            if (allowed.IsFailure)
            {
                return allowed;
            }
        }

        await accounts.SetLoginEnabledAsync(userId, enabled: false, cancellationToken);

        return Result.Success();
    }

    /// <summary>
    /// Erases someone who never did any work here, with their login (a person added by mistake, or who never started).
    /// Anyone who has scanned, assigned or changed a record stays on it as the one who did it, so they can only be
    /// stopped. Super admins only, under the same guards as stopping: not yourself, and never the last super admin.
    /// </summary>
    public async Task<Result> DeleteAsync(long userId, CancellationToken cancellationToken = default)
    {
        var me = await access.CurrentAsync(cancellationToken);
        if (!me.IsSuperAdmin)
        {
            return Above;
        }

        var person = await FindAsync(me, userId, cancellationToken);
        if (person.IsFailure)
        {
            return person.Error!;
        }

        if (person.Value.Person.IsSuperAdmin)
        {
            var allowed = MayLeaveAdmins(person.Value);
            if (allowed.IsFailure)
            {
                return allowed;
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.StaffMembers.Where(s => s.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        var deleted = await accounts.DeleteLoginAsync(userId, cancellationToken);
        if (deleted.IsFailure)
        {
            return Error.Conflict(
                "staff.delete",
                $"{person.Value.Person.Name} has worked here, so they stay on the record of what they did. Stop them instead.");
        }

        await transaction.CommitAsync(cancellationToken);

        return Result.Success();
    }

    public async Task<Result> LetBackInAsync(long userId, CancellationToken cancellationToken = default)
    {
        var person = await FindAsync(await access.CurrentAsync(cancellationToken), userId, cancellationToken);
        if (person.IsFailure)
        {
            return person.Error!;
        }

        await accounts.SetLoginEnabledAsync(userId, enabled: true, cancellationToken);

        return Result.Success();
    }

    /// <summary>A new first password for someone who has lost theirs; shown once.</summary>
    public async Task<Result<string>> ResetPasswordAsync(long userId, CancellationToken cancellationToken = default)
    {
        var person = await FindAsync(await access.CurrentAsync(cancellationToken), userId, cancellationToken);
        if (person.IsFailure)
        {
            return person.Error!;
        }

        var password = Passwords.New();
        var reset = await accounts.ResetPasswordAsync(userId, password, cancellationToken);

        return reset.IsFailure ? reset.Error! : password;
    }

    /// <summary>
    /// Reads what was asked: the name, and the job (null for a super admin) with the hub it is worked at. Refuses what
    /// <paramref name="me"/> may not hand out: a super admin unless they are one, a permission they do not hold, a
    /// hub other than their own from a hub.
    /// </summary>
    private async Task<Result<(string Name, StaffJob? Job, long? HubId)>> ReadAsync(
        StaffRights me,
        string? name,
        string? job,
        long? hubId,
        StaffPermissions extra,
        CancellationToken cancellationToken)
    {
        var trimmed = name.NullIfBlank();
        if (trimmed is null || trimmed.Length > MaxNameLength)
        {
            return Error.Validation("staff.name", $"Enter their name, at most {MaxNameLength} characters.");
        }

        if (job == SuperAdmin)
        {
            return me.IsSuperAdmin
                ? (trimmed, null, null)
                : Error.Validation("staff.superAdmin", "Only a super admin makes super admins.");
        }

        if (!Enum.TryParse<StaffJob>(job, out var chosen) || !Enum.IsDefined(chosen) || int.TryParse(job, out _))
        {
            return Error.Validation("staff.job", "Choose their job.");
        }

        var place = chosen.AtHub() ? hubId : null;
        if (!me.AtHeadOffice && (!chosen.AtHub() || place != me.HubId))
        {
            return Error.Validation("staff.hub", "From a hub you add people to your own hub's desks only.");
        }

        if (place is not null && !await db.Hubs.AnyAsync(h => h.Id == place && !h.Archived, cancellationToken))
        {
            return Error.Validation("staff.hub", $"{chosen.DisplayName()} is a job at one hub. Choose the hub.");
        }

        var checkedRow = StaffMember.Add(0, place, chosen, extra);
        if (checkedRow.IsFailure)
        {
            return checkedRow.Error!;
        }

        if (!me.Holds(checkedRow.Value.Permissions))
        {
            return Error.Validation("staff.permissions", "You can only give someone what you may do yourself.");
        }

        return (trimmed, chosen, place);
    }

    /// <summary>
    /// Whether this super admin may stop being one who can sign in: not when it is the one asking, and not when they
    /// are the last one.
    /// </summary>
    private static Result MayLeaveAdmins((StaffPerson Person, IReadOnlyList<StaffPerson> All) person)
    {
        var activeAdmins = person.All.Count(p => p is { IsSuperAdmin: true, Stopped: false });

        return !person.Person.Stopped && activeAdmins <= 1 ? LastAdmin : Result.Success();
    }

    /// <summary>
    /// The person, if <paramref name="me"/> may manage them, with everyone else for the last-admin check. Someone they
    /// may not see is not found; themselves and someone above them are refused.
    /// </summary>
    private async Task<Result<(StaffPerson Person, IReadOnlyList<StaffPerson> All)>> FindAsync(
        StaffRights me,
        long userId,
        CancellationToken cancellationToken)
    {
        var all = await AllAsync(cancellationToken);
        var person = all.SingleOrDefault(p => p.UserId == userId);
        if (person is null || !MaySee(me, person))
        {
            return NotFound;
        }

        if (person.UserId == currentUser.UserId)
        {
            return NotYourself;
        }

        return MayManage(me, person) ? (person, all) : Above;
    }

    /// <summary>Every super admin and staff login of this courier, with their job and hub.</summary>
    private async Task<IReadOnlyList<StaffPerson>> AllAsync(CancellationToken cancellationToken)
    {
        // Only super admins and staff: a rider's or a merchant's login is never found here, whatever id is posted
        var logins = await accounts.InRolesAsync([Roles.TenantAdmin, Roles.HubStaff], cancellationToken);
        var rows = await db.StaffMembers.AsNoTracking().ToDictionaryAsync(s => s.UserId, cancellationToken);
        var hubs = await db.Hubs.AsNoTracking().ToDictionaryAsync(h => h.Id, h => h.Name, cancellationToken);
        var tenant = tenantContext.Require();

        return
        [
            .. logins.Select(login =>
            {
                var superAdmin = login.Role == Roles.TenantAdmin;
                var row = superAdmin ? null : rows.GetValueOrDefault(login.UserId);

                return new StaffPerson(
                    login.UserId,
                    login.Name,
                    login.Email,
                    superAdmin,
                    row?.Job,
                    row?.Permissions ?? StaffPermissions.None,
                    row?.HubId,
                    row?.HubId is { } hubId ? hubs.GetValueOrDefault(hubId) : null,
                    login.Stopped,
                    tenant.Local(login.Created));
            })
        ];
    }
}
