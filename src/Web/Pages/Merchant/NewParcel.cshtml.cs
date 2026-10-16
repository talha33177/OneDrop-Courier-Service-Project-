using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Abstractions;
using Application.Common;
using Application.Merchants.Account;
using Application.Merchants.Admin;
using Application.Network.ListAreas;
using Application.Parcels.CreateParcel;
using Application.Parcels.Quote;
using Domain.Pricing;
using Web.Display;

namespace Web.Pages.Merchant;

/// <summary>
/// The merchant books a parcel by hand, through the same handler as the API. Each form carries its own idempotency key,
/// so pressing the button twice books one parcel. The charges are quoted live as the form is filled in.
/// </summary>
public class NewParcelModel(
    CreateParcelHandler handler,
    QuoteHandler quotes,
    ListAreasHandler areas,
    MerchantAccountHandler account,
    IMapLinks mapLinks,
    ITenantContext tenantContext,
    TimeProvider time) : PageModel
{
    [BindProperty]
    public ParcelForm Input { get; set; } = new();

    [BindProperty]
    public string FormKey { get; set; } = Guid.NewGuid().ToString("N");

    public IReadOnlyList<AreaItem> Areas { get; private set; } = [];

    public IReadOnlyList<PickupPointView> Points { get; private set; } = [];

    public bool CanBook { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(string? next, CancellationToken cancellationToken)
    {
        var booked = await handler.HandleAsync(Input.ToCommand($"form-{FormKey}"), cancellationToken);
        if (booked.IsFailure)
        {
            ModelState.Add(booked.Error!, nameof(Input));
            await LoadAsync(cancellationToken);

            return Page();
        }

        var tenant = tenantContext.Require();
        var pickup = booked.Value.PickupOn is { } day
            ? $" It goes {Statuses.PickupWith(day, tenant.Today(time.GetUtcNow().UtcDateTime), tenant.PickupTime)}."
            : "";
        TempData["Done"] = $"Parcel {booked.Value.TrackingCode} is booked for {booked.Value.Area}. Charge ৳{booked.Value.TotalCharge:N0}.{pickup}";

        return next == "another"
            ? RedirectToPage()
            : RedirectToPage("/Merchant/Parcel", new { code = booked.Value.TrackingCode });
    }

    /// <summary>
    /// The live quote beside the form, as JSON, with what each speed and kind of contents adds on the route and how
    /// soon each speed delivers (missing where it is not offered). A speed or contents the route does not take is
    /// quoted at the courier's defaults, with the reason, so the form can say why.
    /// </summary>
    public async Task<IActionResult> OnGetQuoteAsync(
        long? areaId,
        long? pickupPointId,
        decimal weightKg,
        decimal codAmount,
        string? speed,
        string? productType,
        CancellationToken cancellationToken)
    {
        var quote = await quotes.QuoteAsync(areaId, null, pickupPointId, weightKg, codAmount, speed, productType, cancellationToken);
        string? problem = null;
        if (quote.Error?.Code is "parcel.speed" or "parcel.productType")
        {
            problem = quote.Error.Message;
            quote = await quotes.QuoteAsync(areaId, null, pickupPointId, weightKg, codAmount, cancellationToken: cancellationToken);
        }

        if (quote.IsFailure)
        {
            return BadRequest();
        }

        var q = quote.Value;

        return new JsonResult(new
        {
            service = q.ServiceArea.DisplayName(),
            q.DeliveryHub,
            q.DeliveryCharge,
            q.CodCharge,
            q.Total,
            q.ReturnCharge,
            when = q.DeliveryDays is { } days ? Statuses.AfterPickup(days) : null,
            q.SpeedCharge,
            q.ProductCharge,
            q.SpeedName,
            productName = q.ProductName,
            speeds = q.Speeds.ToDictionary(
                o => o.Code,
                o => new { extra = o.Charge, when = o.DeliveryDays is { } d ? Statuses.AfterPickup(d) : null }),
            products = q.Contents.ToDictionary(o => o.Code, o => o.Charge),
            problem
        });
    }

    /// <summary>
    /// The pin in what the merchant pasted, as JSON: coordinates, a Google Maps link that holds them, or a short
    /// maps.app.goo.gl link, which is opened first. A link to a named place without coordinates comes back as the
    /// place's name for the map to look up.
    /// </summary>
    public async Task<IActionResult> OnGetPinAsync(string? link, CancellationToken cancellationToken)
    {
        return new JsonResult(await PastedPin.ReadAsync(mapLinks, link, cancellationToken));
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Areas = await areas.HandleAsync(cancellationToken);
        var mine = await account.GetAsync(cancellationToken);
        Points = mine.PickupPoints;
        CanBook = mine.Status == Domain.Merchants.MerchantStatus.Active;
    }
}
