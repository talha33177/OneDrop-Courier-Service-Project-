using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Merchants.Account;
using Application.Merchants.Admin;
using Application.Network.ListAreas;
using Application.Pricing.DeliveryOptions;
using Application.Pricing.Rates;

namespace Web.Pages.Merchant;

/// <summary>The courier's rate card, its delivery types and a calculator that prices a parcel from the merchant's pickup point.</summary>
public class PricingModel(
    RatesHandler rates,
    DeliveryOptionsHandler deliveryOptions,
    ListAreasHandler areas,
    MerchantAccountHandler account) : PageModel
{
    public IReadOnlyList<RateRow> Rates { get; private set; } = [];

    public IReadOnlyList<OptionRow> DeliveryTypes { get; private set; } = [];

    public IReadOnlyList<AreaItem> Areas { get; private set; } = [];

    public IReadOnlyList<PickupPointView> Points { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Rates = await rates.ListAsync(cancellationToken);
        DeliveryTypes = await deliveryOptions.ListAsync(cancellationToken: cancellationToken);
        Areas = await areas.HandleAsync(cancellationToken);
        Points = (await account.GetAsync(cancellationToken)).PickupPoints;
    }
}
