using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Parcels.Browse;
using Application.Parcels.ParcelActions;
using Application.Parcels.Requests;
using Domain.Common;
using Domain.Parcels;
using Web.Authentication;

namespace Web.Pages.Hub;

/// <summary>
/// One parcel for the courier's staff, who can weigh it, flag a problem on it for the courier to look at, or ask for it
/// to go back to its merchant.
/// </summary>
public class ParcelModel(ParcelDetailsHandler details, ParcelActionsHandler actions, ParcelRequestsHandler requests) : PageModel
{
    public ParcelView Parcel { get; private set; } = null!;

    /// <summary>The merchant's request about the parcel still waiting for the admins' answer, if any.</summary>
    public RequestRow? OpenRequest { get; private set; }

    /// <summary>
    /// Whether this person's desk uses one of the page's buttons. A hub's own staff are still refused a parcel at
    /// another hub when they press it.
    /// </summary>
    public bool MayUse(string handler)
    {
        return MayOpen($"/Hub/Parcel/{handler}");
    }

    public bool MayOpen(string page)
    {
        return StaffPages.Rights(HttpContext) is { } rights && StaffPages.MayOpen(rights, page);
    }

    public async Task<IActionResult> OnGetAsync(string code, CancellationToken cancellationToken)
    {
        var found = await details.GetAsync(code, cancellationToken);
        if (found.IsFailure)
        {
            return NotFound();
        }

        Parcel = found.Value;
        OpenRequest = (await requests.ForParcelAsync(code, cancellationToken)).FirstOrDefault(r => r.Status == ParcelRequestStatus.Open);

        return Page();
    }

    public async Task<IActionResult> OnPostReweighAsync(string code, decimal weightKg, CancellationToken cancellationToken)
    {
        var grams = (int)Math.Round(weightKg * 1000, MidpointRounding.AwayFromZero);
        var weighed = await actions.ReweighAsync(code, grams, cancellationToken);
        if (weighed.IsFailure && weighed.Error!.Type == Domain.Common.ErrorType.NotFound)
        {
            return NotFound();
        }

        TempData[weighed.IsSuccess ? "Done" : "Problem"] = weighed.IsSuccess
            ? $"{code} is now charged on {Weight.Kg(grams)}."
            : weighed.Error!.Message;

        return RedirectToPage(new { code });
    }

    public async Task<IActionResult> OnPostFlagAsync(string code, ParcelIssue issue, string? note, CancellationToken cancellationToken)
    {
        var flagged = await actions.FlagAsync(code, issue, note, cancellationToken);
        if (flagged.IsFailure && flagged.Error!.Type == ErrorType.NotFound)
        {
            return NotFound();
        }

        TempData[flagged.IsSuccess ? "Done" : "Problem"] = flagged.IsSuccess
            ? $"{code} is flagged {issue.DisplayName().ToLowerInvariant()}. It stays off riders' runs until the flag is cleared."
            : flagged.Error!.Message;

        return RedirectToPage(new { code });
    }

    public async Task<IActionResult> OnPostClearFlagAsync(string code, string? note, CancellationToken cancellationToken)
    {
        var cleared = await actions.ClearFlagAsync(code, note, cancellationToken);
        if (cleared.IsFailure && cleared.Error!.Type == ErrorType.NotFound)
        {
            return NotFound();
        }

        TempData[cleared.IsSuccess ? "Done" : "Problem"] = cleared.IsSuccess
            ? $"The flag on {code} is cleared; it can go out to a rider again."
            : cleared.Error!.Message;

        return RedirectToPage(new { code });
    }

    public async Task<IActionResult> OnPostReturnAsync(string code, string? reason, CancellationToken cancellationToken)
    {
        var returned = await actions.RequestReturnAsync(code, reason, cancellationToken);
        if (returned.IsFailure && returned.Error!.Type == Domain.Common.ErrorType.NotFound)
        {
            return NotFound();
        }

        TempData[returned.IsSuccess ? "Done" : "Problem"] = returned.IsSuccess ? $"{code} is going back to its merchant." : returned.Error!.Message;

        return RedirectToPage(new { code });
    }
}
