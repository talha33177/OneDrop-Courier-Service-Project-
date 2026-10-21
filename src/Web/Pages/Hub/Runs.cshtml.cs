using Microsoft.AspNetCore.Mvc;
using Application.Hubs.HubOverview;
using Application.Hubs.Runs;

namespace Web.Pages.Hub;

/// <summary>Rider closing: count each rider's cash against their deliveries and take back what they could not deliver.</summary>
public class RunsModel(HubOverviewHandler hubs, RunsHandler runs) : HubPage(hubs)
{
    public IReadOnlyList<RunRow> Runs { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!await LoadHubAsync(cancellationToken))
        {
            return NotFound();
        }

        if (Hub is not null)
        {
            Runs = await runs.ListAsync(Hub.Code, cancellationToken) ?? [];
        }

        return Page();
    }

    public async Task<IActionResult> OnPostCloseAsync(long id, decimal received, CancellationToken cancellationToken)
    {
        if (!await LoadHubAsync(cancellationToken) || Hub is null)
        {
            return NotFound();
        }

        var closed = await runs.CloseAsync(Hub.Code, id, received, cancellationToken);
        if (closed.IsFailure && closed.Error!.Type == Domain.Common.ErrorType.NotFound)
        {
            return NotFound();
        }

        TempData[closed.IsSuccess ? "Done" : "Problem"] = closed.IsSuccess
            ? closed.Value > 0
                ? $"Run closed ৳{closed.Value:N0} short. The parcels not delivered are back at the hub; the shortfall waits for the hub manager on the Cash page."
                : "Run closed. The parcels not delivered are back at the hub and the cash is the hub's to pay in."
            : closed.Error!.Message;

        return RedirectToPage(new { hub = Hub.Code });
    }
}
