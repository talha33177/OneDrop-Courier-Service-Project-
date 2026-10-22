using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Delivery.Riders;
using Application.Hubs;
using Domain.Common;
using Domain.Delivery;
using Web.Authentication;

namespace Web.Pages.Admin;

/// <summary>
/// The courier's riders by hub with what they ride and today's work; add a rider with a login, edit one, stop or restart
/// one.
/// </summary>
public class RidersModel(AdminRidersHandler riders, HubDirectory hubs) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long? HubId { get; set; }

    [BindProperty(SupportsGet = true)]
    public long? Edit { get; set; }

    public IReadOnlyList<RiderRow> Rows { get; private set; } = [];

    public IReadOnlyList<HubItem> Hubs { get; private set; } = [];

    public string? Problem { get; private set; }

    /// <summary>Deleting a rider is the super admin's alone.</summary>
    public bool IsSuperAdmin => StaffPages.Rights(HttpContext)?.IsSuperAdmin == true;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAddAsync(
        string? name,
        string? phone,
        long hubId,
        Vehicle vehicle,
        string? email,
        string? password,
        CancellationToken cancellationToken)
    {
        var added = await riders.AddAsync(new NewRider(name, phone, hubId, vehicle, email, password), cancellationToken);
        if (added.IsSuccess)
        {
            TempData["Done"] = $"{name} is added. They sign in with {email}.";
            if ((await hubs.ListAsync(cancellationToken)).FirstOrDefault(hub => hub.Id == hubId) is { } home)
            {
                TempData["DoneLink"] = $"/Hub/Assign?hub={home.Code}";
                TempData["DoneLinkText"] = $"Hand them parcels at {home.Name}";
            }

            return RedirectToPage();
        }

        Problem = added.Error!.Message;
        await LoadAsync(cancellationToken);

        return Page();
    }

    public async Task<IActionResult> OnPostEditAsync(
        long id,
        string? name,
        string? phone,
        long hubId,
        Vehicle vehicle,
        CancellationToken cancellationToken)
    {
        return Answer(await riders.EditAsync(id, name, phone, hubId, vehicle, cancellationToken), "Rider saved.");
    }

    public async Task<IActionResult> OnPostActiveAsync(long id, bool active, CancellationToken cancellationToken)
    {
        return Answer(await riders.SetActiveAsync(id, active, cancellationToken), active ? "Rider restarted." : "Rider stopped: no new work is assigned to them.");
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id, CancellationToken cancellationToken)
    {
        return Answer(await riders.DeleteAsync(id, cancellationToken), "Rider deleted.");
    }

    private IActionResult Answer(Result result, string done)
    {
        if (result.IsFailure && result.Error!.Type == ErrorType.NotFound)
        {
            return NotFound();
        }

        TempData[result.IsSuccess ? "Done" : "Problem"] = result.IsSuccess ? done : result.Error!.Message;

        return RedirectToPage(new { hubId = HubId });
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Rows = await riders.ListAsync(HubId, cancellationToken);
        Hubs = await hubs.ListAsync(cancellationToken);
    }
}
