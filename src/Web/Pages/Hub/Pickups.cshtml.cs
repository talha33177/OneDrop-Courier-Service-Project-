using Microsoft.AspNetCore.Mvc;
using Application.Abstractions;
using Application.Common;
using Application.Delivery.Pickups;
using Application.Hubs.HubOverview;
using Application.Hubs.AssignParcels;

namespace Web.Pages.Hub;

/// <summary>
/// The pickups this hub collects: the day's list of merchants' pickup points (scheduled as parcels are booked), the
/// rider each is given, and tomorrow's list so far (points the van has been to today).
/// </summary>
public class PickupsModel(
    HubOverviewHandler hubs,
    PickupsHandler pickups,
    AssignParcelsHandler riders,
    ITenantContext tenantContext,
    TimeProvider time) : HubPage(hubs)
{
    public IReadOnlyList<PickupRow> Requests { get; private set; } = [];

    public IReadOnlyList<RiderLoad> Riders { get; private set; } = [];

    /// <summary>The hub's pickup van riders: the only ones sent to merchants.</summary>
    public IReadOnlyList<RiderLoad> Vans => [.. Riders.Where(r => r.CollectsPickups)];

    public DateOnly Today { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!await LoadHubAsync(cancellationToken))
        {
            return NotFound();
        }

        Today = tenantContext.Require().Today(time.GetUtcNow().UtcDateTime);
        if (Hub is not null)
        {
            Requests = await pickups.ForHubAsync(Hub.Code, cancellationToken) ?? [];
            Riders = await riders.RidersAsync(Hub.Code, cancellationToken);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAssignAsync(long id, long riderId, CancellationToken cancellationToken)
    {
        if (!await LoadHubAsync(cancellationToken) || Hub is null)
        {
            return NotFound();
        }

        var assigned = await pickups.AssignAsync(Hub.Code, id, riderId, cancellationToken);
        if (assigned.IsFailure && assigned.Error!.Type == Domain.Common.ErrorType.NotFound)
        {
            return NotFound();
        }

        TempData[assigned.IsSuccess ? "Done" : "Problem"] = assigned.IsSuccess ? "Rider assigned to the pickup." : assigned.Error!.Message;

        return RedirectToPage(new { hub = Hub.Code });
    }

    /// <summary>One rider, the pickup truck's driver, takes every pickup of the day that has no rider yet.</summary>
    public async Task<IActionResult> OnPostAssignAllAsync(DateOnly day, long riderId, CancellationToken cancellationToken)
    {
        if (!await LoadHubAsync(cancellationToken) || Hub is null)
        {
            return NotFound();
        }

        var assigned = await pickups.AssignAllAsync(Hub.Code, day, riderId, cancellationToken);
        if (assigned.IsFailure)
        {
            TempData["Problem"] = assigned.Error!.Message;
        }
        else
        {
            var round = assigned.Value;
            TempData["Done"] = $"{round.Assigned} pickup{(round.Assigned == 1 ? "" : "s")} given to the rider.";
            if (round.Left > 0)
            {
                TempData["Problem"] = $"{round.Left} pickup{(round.Left == 1 ? "" : "s")} stay{(round.Left == 1 ? "s" : "")} on the list: {round.Why}";
            }
        }

        return RedirectToPage(new { hub = Hub.Code });
    }
}
