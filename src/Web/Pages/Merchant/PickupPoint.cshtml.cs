using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Abstractions;
using Application.Merchants.Account;
using Application.Merchants.Admin;
using Application.Network.ListAreas;
using Domain.Common;
using Web.Display;

namespace Web.Pages.Merchant;

/// <summary>
/// One pickup point: added or changed with its door pinned on the map, so the pickup van is guided to it and the stop
/// sits on the rider's route. Nothing here is a merchant's parcel, so the merchant filter does the keeping: a point of
/// another account is simply not found.
/// </summary>
public class PickupPointModel(
    MerchantAccountHandler account,
    ListAreasHandler areas,
    IMapLinks mapLinks) : PageModel
{
    public IReadOnlyList<AreaItem> Areas { get; private set; } = [];

    public PickupPointView? Point { get; private set; }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!await LoadAsync(cancellationToken))
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(
        long areaId,
        string? name,
        string? address,
        string? phone,
        decimal? latitude,
        decimal? longitude,
        CancellationToken cancellationToken)
    {
        var saved = await account.SavePickupPointAsync(Id, areaId, name, address, phone, latitude, longitude, cancellationToken);
        if (saved.IsFailure)
        {
            if (saved.Error!.Type == ErrorType.NotFound)
            {
                return NotFound();
            }

            TempData["Problem"] = saved.Error.Message;
            await LoadAsync(cancellationToken);

            return Page();
        }

        TempData["Done"] = Id is null ? "Pickup point added." : "Pickup point saved.";

        return RedirectToPage("/Merchant/Settings");
    }

    /// <summary>The pin in what the merchant pasted, for the pin box's "Use it".</summary>
    public async Task<IActionResult> OnGetPinAsync(string? link, CancellationToken cancellationToken)
    {
        return new JsonResult(await PastedPin.ReadAsync(mapLinks, link, cancellationToken));
    }

    /// <summary>False when an id was given for a point the account does not have.</summary>
    private async Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        Areas = await areas.HandleAsync(cancellationToken);
        var mine = await account.GetAsync(cancellationToken);
        Point = Id is null ? null : mine.PickupPoints.SingleOrDefault(p => p.Id == Id);

        return Id is null || Point is not null;
    }
}
