using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Dashboards;
using Application.Notifications.Bell;

namespace Web.Pages.Admin;

/// <summary>
/// The courier admin's home, read top to bottom: what waits for the admin's decision (and the setup still missing), the
/// parcels' day across every hub as numbered steps that open the hub with that work, then how today is going.
/// </summary>
public class IndexModel(DashboardHandler dashboard, AdminBellHandler bell) : PageModel
{
    public AdminDashboard Data { get; private set; } = null!;

    public IReadOnlyList<Notice> Waiting { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Data = await dashboard.AdminAsync(cancellationToken);
        Waiting = (await bell.ForAsync(null, cancellationToken)).Notices;
    }
}
