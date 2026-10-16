using Domain.Staff;

namespace Domain.Tests;

/// <summary>
/// A staff member's rules: a hub job is worked at one hub and a head office job at none, the job's own permissions
/// always come with it, more can be ticked on top, and head office work is never given at a hub.
/// </summary>
public class StaffMemberTests
{
    [Fact]
    public void A_hub_job_starts_with_its_own_desk_and_may_be_given_more()
    {
        var receiving = StaffMember.Add(42, 7, StaffJob.Receiving, StaffPermissions.None).Value;

        Assert.Equal(7, receiving.HubId);
        Assert.True(receiving.May(StaffPermissions.Receive));
        Assert.False(receiving.May(StaffPermissions.Dispatch));
        Assert.False(receiving.May(StaffPermissions.CashDesk));

        // Covers line-haul too: the extra tick comes on top of the job's own
        Assert.True(receiving.Change(7, StaffJob.Receiving, StaffPermissions.Dispatch).IsSuccess);
        Assert.True(receiving.May(StaffPermissions.Dispatch));
        Assert.True(receiving.May(StaffPermissions.Receive));
        Assert.Equal(StaffPermissions.Receive | StaffPermissions.Dispatch, receiving.Permissions);
    }

    [Fact]
    public void A_hub_job_needs_a_hub_and_a_head_office_job_none()
    {
        Assert.Equal("staff.hub", StaffMember.Add(42, null, StaffJob.CashDesk, StaffPermissions.None).Error!.Code);
        Assert.Equal("staff.hub", StaffMember.Add(42, 7, StaffJob.Accounts, StaffPermissions.None).Error!.Code);
        Assert.Equal("staff.job", StaffMember.Add(42, 7, (StaffJob)99, StaffPermissions.None).Error!.Code);
        Assert.True(StaffMember.Add(42, null, StaffJob.Accounts, StaffPermissions.None).IsSuccess);
    }

    [Fact]
    public void Head_office_work_is_never_given_at_a_hub()
    {
        Assert.Equal(
            "staff.permissions",
            StaffMember.Add(42, 7, StaffJob.HubManager, StaffPermissions.Finance).Error!.Code);
        Assert.Equal(
            "staff.permissions",
            StaffMember.Add(42, 7, StaffJob.Receiving, (StaffPermissions)4096).Error!.Code);

        // At head office the same tick is fine, and a hub manager may manage their own hub's people
        Assert.True(StaffMember.Add(42, null, StaffJob.OperationsManager, StaffPermissions.Finance).Value.May(StaffPermissions.Finance));
        Assert.True(StaffMember.Add(42, 7, StaffJob.HubManager, StaffPermissions.None).Value.May(StaffPermissions.Staff));
    }

    [Fact]
    public void Every_job_has_a_place_and_starts_within_what_its_place_allows()
    {
        foreach (var job in StaffJobs.All)
        {
            var hub = job.AtHub() ? 7L : (long?)null;
            var member = StaffMember.Add(42, hub, job, StaffPermissions.None);

            Assert.True(member.IsSuccess, job.ToString());
            Assert.Equal(job.Preset(), member.Value.Permissions);
            Assert.False(string.IsNullOrWhiteSpace(job.DisplayName()));
            Assert.False(string.IsNullOrWhiteSpace(job.What()));
        }

        // Relief staff keep exactly what every hub login could do before jobs existed
        Assert.Equal(StaffPermissions.HubWork, StaffJob.Relief.Preset());
        Assert.False(StaffJob.Relief.AtHub());
        Assert.Equal(StaffPermissions.None, StaffJob.CustomerSupport.Preset());
    }
}
