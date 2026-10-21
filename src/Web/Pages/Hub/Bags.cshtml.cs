using Microsoft.AspNetCore.Mvc;
using Application.Hubs.Bags;
using Application.Hubs.HubOverview;
using Domain.Common;
using Domain.Staff;

namespace Web.Pages.Hub;

/// <summary>
/// Bags between hubs. Line-haul sees the open bag for each hub it is filling on the scanner, what still waits to go in a
/// bag, takes a parcel out and seals a bag onto the truck; receiving sees the bags on their way here, ticks parcels off
/// on the scanner and closes a bag, which flags whatever it did not find as missing.
/// </summary>
public class BagsModel(HubOverviewHandler hubs, BagsHandler bags) : HubPage(hubs)
{
    public HubBags? Data { get; private set; }

    public bool MaySeal => Rights.May(StaffPermissions.Dispatch);

    public bool MayClose => Rights.May(StaffPermissions.Receive);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!await LoadHubAsync(cancellationToken))
        {
            return NotFound();
        }

        if (Hub is not null)
        {
            Data = await bags.HubAsync(Hub.Code, cancellationToken);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSealAsync(string number, string? vehicle, CancellationToken cancellationToken)
    {
        if (!await LoadHubAsync(cancellationToken) || Hub is null)
        {
            return NotFound();
        }

        var sealedBag = await bags.SealAsync(Hub.Code, number, vehicle, cancellationToken);

        return Answer(sealedBag, $"Bag {number} is sealed and on its way. Print its manifest for the driver.");
    }

    public async Task<IActionResult> OnPostTakeOutAsync(string number, string code, CancellationToken cancellationToken)
    {
        if (!await LoadHubAsync(cancellationToken) || Hub is null)
        {
            return NotFound();
        }

        var taken = await bags.TakeOutAsync(Hub.Code, number, code, cancellationToken);

        return Answer(taken, $"{code} is out of bag {number} and stays at the hub.");
    }

    public async Task<IActionResult> OnPostCloseAsync(string number, CancellationToken cancellationToken)
    {
        if (!await LoadHubAsync(cancellationToken) || Hub is null)
        {
            return NotFound();
        }

        var closed = await bags.CloseAsync(Hub.Code, number, cancellationToken);

        return Answer(
            closed,
            closed.IsSuccess && closed.Value > 0
                ? $"Bag {number} is closed. {closed.Value} parcel{(closed.Value == 1 ? " was" : "s were")} not in it and {(closed.Value == 1 ? "is" : "are")} flagged exceptional as missing."
                : $"Bag {number} is closed.");
    }

    private IActionResult Answer(Result result, string done)
    {
        if (result.IsFailure && result.Error!.Type == ErrorType.NotFound && result.Error.Code == BagsHandler.NotFound.Code)
        {
            return NotFound();
        }

        TempData[result.IsSuccess ? "Done" : "Problem"] = result.IsSuccess ? done : result.Error!.Message;

        return RedirectToPage(new { hub = Hub!.Code });
    }
}
