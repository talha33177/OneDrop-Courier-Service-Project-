using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Pricing.DeliveryOptions;
using Application.Pricing.Rates;
using Domain.Pricing;

namespace Web.Pages.Admin;

/// <summary>
/// The delivery speeds merchants choose from and the kinds of contents they declare: the admin adds them, prices each
/// per service area, orders them, picks the default and takes them off. A change prices parcels booked from now on.
/// </summary>
public class DeliveryTypesModel(DeliveryOptionsHandler options, RatesHandler rates) : PageModel
{
    public static readonly ServiceArea[] Areas = Enum.GetValues<ServiceArea>();

    public IReadOnlyList<OptionRow> Options { get; private set; } = [];

    public IReadOnlyList<RateRow> Rates { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Options = await options.ListAsync(withArchived: true, cancellationToken);
        Rates = await rates.ListAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAddAsync(DeliveryOptionKind kind, OptionForm form, CancellationToken cancellationToken)
    {
        var added = await options.AddAsync(kind, form.Values, form.Prices, cancellationToken);
        TempData[added.IsSuccess ? "Done" : "Problem"] = added.IsSuccess
            ? $"{added.Value} added. Merchants can choose it for parcels booked from now on."
            : added.Error!.Message;

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSaveAsync(long id, OptionForm form, CancellationToken cancellationToken)
    {
        var saved = await options.ChangeAsync(id, form.Values, form.Prices, cancellationToken);
        TempData[saved.IsSuccess ? "Done" : "Problem"] = saved.IsSuccess
            ? $"{saved.Value} saved. Parcels booked from now on use it."
            : saved.Error!.Message;

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostMoveAsync(long id, bool up, CancellationToken cancellationToken)
    {
        var moved = await options.MoveAsync(id, up, cancellationToken);
        if (moved.IsFailure)
        {
            TempData["Problem"] = moved.Error!.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDefaultAsync(long id, CancellationToken cancellationToken)
    {
        var made = await options.MakeDefaultAsync(id, cancellationToken);
        TempData[made.IsSuccess ? "Done" : "Problem"] = made.IsSuccess
            ? $"{made.Value} is now what merchants get when they choose nothing, at no extra."
            : made.Error!.Message;

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostArchiveAsync(long id, CancellationToken cancellationToken)
    {
        var archived = await options.ArchiveAsync(id, cancellationToken);
        TempData[archived.IsSuccess ? "Done" : "Problem"] = archived.IsSuccess
            ? $"{archived.Value} is no longer offered. Parcels already booked with it keep it."
            : archived.Error!.Message;

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRestoreAsync(long id, CancellationToken cancellationToken)
    {
        var restored = await options.RestoreAsync(id, cancellationToken);
        TempData[restored.IsSuccess ? "Done" : "Problem"] = restored.IsSuccess
            ? $"{restored.Value} is offered again, with the prices it had."
            : restored.Error!.Message;

        return RedirectToPage();
    }

    /// <summary>
    /// One option's fields as the page posts them: the service areas ticked as offered, and the charges and days in
    /// service area order. An area ticked with no charge is offered at no extra; one not ticked is not offered.
    /// </summary>
    public sealed class OptionForm
    {
        public ServiceArea[] OfferedIn { get; set; } = [];

        public string? Name { get; set; }

        public string? Description { get; set; }

        public string? Handling { get; set; }

        public string? Icon { get; set; }

        public bool GoesFirst { get; set; }

        public decimal?[] Charges { get; set; } = [];

        public int?[] Days { get; set; } = [];

        public DeliveryOptionValues Values => new(Name, Description, Handling, Icon, GoesFirst);

        public IReadOnlyList<OptionPriceInput> Prices =>
        [
            .. Areas.Select((area, i) => OfferedIn.Contains(area)
                ? new OptionPriceInput(area, (i < Charges.Length ? Charges[i] : null) ?? 0, i < Days.Length ? Days[i] : null)
                : new OptionPriceInput(area, null, null))
        ];
    }
}
