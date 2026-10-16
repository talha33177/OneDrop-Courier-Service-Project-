using Domain.Common;

namespace Domain.Staff;

/// <summary>
/// One of the courier's staff below super admin: where they work and what they may do. Where is one hub, or head
/// office (no hub), which reaches every hub; what is the job's permissions plus whatever the admin ticked. The login
/// itself is an ordinary staff user, stopped and let back in on the login; super admins have no row, their role says
/// it all.
/// </summary>
public class StaffMember : TenantEntity
{
    private StaffMember()
    {
    }

    public long UserId { get; private set; }

    /// <summary>The hub they work at; null for head office, which works at every hub.</summary>
    public long? HubId { get; private set; }

    public StaffJob Job { get; private set; }

    public StaffPermissions Permissions { get; private set; }

    public static Result<StaffMember> Add(long userId, long? hubId, StaffJob job, StaffPermissions extra)
    {
        var member = new StaffMember { UserId = userId };
        var changed = member.Change(hubId, job, extra);

        return changed.IsSuccess ? member : changed.Error!;
    }

    /// <summary>A new place, job or ticks. The job's own permissions always come with it.</summary>
    public Result Change(long? hubId, StaffJob job, StaffPermissions extra)
    {
        if (!Enum.IsDefined(job))
        {
            return Error.Validation("staff.job", "Choose their job.");
        }

        if (job.AtHub() && hubId is null)
        {
            return Error.Validation("staff.hub", $"{job.DisplayName()} is a job at one hub. Choose the hub.");
        }

        if (!job.AtHub() && hubId is not null)
        {
            return Error.Validation("staff.hub", $"{job.DisplayName()} works at head office, not at one hub.");
        }

        var permissions = job.Preset() | extra;
        if ((permissions & ~StaffPermissions.All) != 0)
        {
            return Error.Validation("staff.permissions", "Choose only from the permissions listed.");
        }

        if (hubId is not null && (permissions & StaffPermissions.HeadOffice) != 0)
        {
            return Error.Validation(
                "staff.permissions",
                "Operations, merchants, money and setup are head office work. Put them at head office to give those.");
        }

        HubId = hubId;
        Job = job;
        Permissions = permissions;

        return Result.Success();
    }

    /// <summary>Whether they hold any of <paramref name="any"/>; nothing asked is always allowed.</summary>
    public bool May(StaffPermissions any)
    {
        return any == StaffPermissions.None || (Permissions & any) != 0;
    }
}
