using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Hubs;
using Application.Platform.Staff;
using Domain.Common;
using Domain.Staff;
using Web.Authentication;

namespace Web.Pages.Admin;

/// <summary>
/// The courier's own people with a sign-in: add a super admin or a staff member with a job (at one hub or at head
/// office) and a first password shown once, change their job, hub and ticks, stop them or let them back in, and give
/// out a new password. A hub manager sees and adds only their own hub's people here.
/// </summary>
public class StaffModel(StaffHandler handler, HubDirectory hubDirectory) : PageModel
{
    public IReadOnlyList<StaffPerson> People { get; private set; } = [];

    public IReadOnlyList<HubItem> Hubs { get; private set; } = [];

    public StaffRights Me => StaffPages.Rights(HttpContext) ?? StaffRights.Nobody;

    [BindProperty]
    public string? Name { get; set; }

    [BindProperty]
    public string? Email { get; set; }

    [BindProperty]
    public string? Job { get; set; }

    [BindProperty]
    public long? HubId { get; set; }

    [BindProperty]
    public List<StaffPermissions> Extra { get; set; } = [];

    [BindProperty(SupportsGet = true)]
    public long? Edit { get; set; }

    /// <summary>The password of the person just added or just given a new one: shown once, to hand over.</summary>
    public string? FirstPassword { get; private set; }

    public string? PasswordFor { get; private set; }

    public string? Problem { get; private set; }

    /// <summary>The jobs this person may hand out: a hub's desks from a hub, whatever they hold from head office.</summary>
    public IReadOnlyList<StaffJob> Jobs =>
        [.. StaffJobs.All.Where(job => (Me.AtHeadOffice || job.AtHub()) && Me.Holds(job.Preset()))];

    /// <summary>The ticks this person may hand out: only what they hold, and no head office work from a hub.</summary>
    public IReadOnlyList<StaffPermissions> Ticks =>
        [.. StaffJobs.Each.Where(p => Me.Holds(p) && (Me.AtHeadOffice || (p & StaffPermissions.HeadOffice) == 0))];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAddAsync(CancellationToken cancellationToken)
    {
        var extra = Extra.Aggregate(StaffPermissions.None, (all, one) => all | one);
        var added = await handler.AddAsync(new NewStaff(Name, Email, Job, HubId, extra), cancellationToken);
        if (added.IsSuccess)
        {
            FirstPassword = added.Value;
            PasswordFor = Email?.Trim();
            Name = Email = Job = null;
            HubId = null;
            Extra = [];
            ModelState.Clear();
        }
        else
        {
            Problem = added.Error!.Message;
        }

        await LoadAsync(cancellationToken);

        return Page();
    }

    public async Task<IActionResult> OnPostChangeAsync(long id, CancellationToken cancellationToken)
    {
        var extra = Extra.Aggregate(StaffPermissions.None, (all, one) => all | one);
        var changed = await handler.ChangeAsync(id, Name, Job, HubId, extra, cancellationToken);

        // A refusal keeps their row open with the reason in it, where the change was being made
        Edit = id;

        return Answer(changed, "Saved. Their pages follow from their next click.") ?? await AgainAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostStopAsync(long id, CancellationToken cancellationToken)
    {
        return Answer(await handler.StopAsync(id, cancellationToken), "They are stopped and cannot sign in.")
            ?? await AgainAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id, CancellationToken cancellationToken)
    {
        return Answer(await handler.DeleteAsync(id, cancellationToken), "Deleted.") ?? await AgainAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostLetBackInAsync(long id, CancellationToken cancellationToken)
    {
        var allowed = await handler.LetBackInAsync(id, cancellationToken);

        return Answer(allowed, "They can sign in again with the same password.") ?? await AgainAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostResetAsync(long id, CancellationToken cancellationToken)
    {
        var reset = await handler.ResetPasswordAsync(id, cancellationToken);
        await LoadAsync(cancellationToken);
        if (reset.IsFailure)
        {
            Problem = reset.Error!.Message;

            return reset.Error.Type == ErrorType.NotFound ? NotFound() : Page();
        }

        FirstPassword = reset.Value;
        PasswordFor = People.SingleOrDefault(p => p.UserId == id)?.Email;

        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        People = await handler.ListAsync(cancellationToken);
        Hubs = Me.AtHeadOffice
            ? await hubDirectory.ListAsync(cancellationToken)
            : [.. (await hubDirectory.ListAsync(cancellationToken)).Where(hub => hub.Id == Me.HubId)];
    }

    /// <summary>A redirect with the toast when it worked, a 404 for someone not on this page, otherwise null.</summary>
    private IActionResult? Answer(Result result, string done)
    {
        if (result.IsSuccess)
        {
            TempData["Done"] = done;

            return RedirectToPage();
        }

        if (result.Error!.Type == ErrorType.NotFound)
        {
            return NotFound();
        }

        Problem = result.Error.Message;

        return null;
    }

    private async Task<IActionResult> AgainAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);

        return Page();
    }
}
