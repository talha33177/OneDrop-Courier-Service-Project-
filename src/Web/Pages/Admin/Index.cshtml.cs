using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Dashboards;
using Application.Notifications.Bell;
using Application.Platform.Staff;
using Web.Authentication;

namespace Web.Pages.Admin;

/// <summary>
/// The courier admin's home, read top to bottom: what waits for the admin's decision (and the setup still missing), the
/// parcels' day across every hub as numbered steps that open the hub with that work, then how today is going. An
/// operations manager sees it too, with only the decisions and buttons their job opens.
/// </summary>
public class IndexModel(DashboardHandler dashboard, AdminBellHandler bell) : PageModel
{
    public AdminDashboard Data { get; private set; } = null!;

    public IReadOnlyList<Notice> Waiting { get; private set; } = [];

    public bool MayOpen(string page)
    {
        return StaffPages.MayOpen(StaffPages.Rights(HttpContext) ?? StaffRights.Nobody, page);
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var rights = StaffPages.Rights(HttpContext) ?? StaffRights.Nobody;
        Data = await dashboard.AdminAsync(cancellationToken);
        Waiting = (await bell.ForAsync(null, link => StaffPages.MayFollow(rights, link), cancellationToken)).Notices;
    }
}
