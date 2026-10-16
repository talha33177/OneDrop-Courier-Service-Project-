using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Merchants.Moderators;
using Domain.Merchants;

namespace Web.Authentication;

/// <summary>
/// What each page of the merchant panel asks of the person signed in. The account's owner may do everything; a
/// moderator may open only the pages their permission covers. A page missing from this list is the owner's alone,
/// so a new page is shut until it is listed on purpose.
/// </summary>
public static class MerchantPages
{
    public static readonly IReadOnlyDictionary<string, MerchantPermissions> Needs =
        new Dictionary<string, MerchantPermissions>(StringComparer.OrdinalIgnoreCase)
        {
            ["/Merchant/Index"] = MerchantPermissions.Dashboard,
            ["/Merchant/Stats"] = MerchantPermissions.Dashboard,
            ["/Merchant/Parcels"] = MerchantPermissions.Parcels,
            ["/Merchant/Parcel"] = MerchantPermissions.Parcels,
            ["/Merchant/Labels"] = MerchantPermissions.Parcels,
            ["/Merchant/Returns"] = MerchantPermissions.Parcels,
            ["/Merchant/Return"] = MerchantPermissions.Parcels,
            ["/Merchant/Requests"] = MerchantPermissions.Parcels,
            ["/Merchant/Export"] = MerchantPermissions.Parcels,
            ["/Merchant/ByDate"] = MerchantPermissions.Parcels,
            ["/Merchant/NewParcel"] = MerchantPermissions.Booking,
            ["/Merchant/EditParcel"] = MerchantPermissions.Booking,
            ["/Merchant/BulkUpload"] = MerchantPermissions.Booking,
            ["/Merchant/Pickups"] = MerchantPermissions.Booking,
            ["/Merchant/Payments"] = MerchantPermissions.Payments,
            ["/Merchant/Payment"] = MerchantPermissions.Payments,
            ["/Merchant/FraudCheck"] = MerchantPermissions.Tools,
            ["/Merchant/Pricing"] = MerchantPermissions.Tools,
            ["/Merchant/Settings"] = MerchantPermissions.Settings,
            ["/Merchant/PickupPoint"] = MerchantPermissions.Settings,
            ["/Merchant/ApiKeys"] = MerchantPermissions.Settings,
            ["/Merchant/Webhook"] = MerchantPermissions.Settings,
            ["/Merchant/Businesses"] = MerchantPermissions.Settings,
            ["/Merchant/NewBusiness"] = MerchantPermissions.Settings,

            // The account's picture, shown in the sidebar of every page a moderator may open
            ["/Merchant/Picture"] = MerchantPermissions.None
        };

    /// <summary>Where the rights of the person signed in are kept for the rest of the request.</summary>
    public const string RightsKey = "merchant-rights";
}

/// <summary>
/// Holds a moderator to the pages their owner ticked, and shuts out one who has been stopped. The rights are read
/// once and left on the request, so the layout can draw the menu from them without asking again.
/// </summary>
public class MerchantPermissionFilter(MerchantAccess access) : IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context)
    {
        return Task.CompletedTask;
    }

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var rights = await access.CurrentAsync(context.HttpContext.RequestAborted);
        context.HttpContext.Items[MerchantPages.RightsKey] = rights;
        if (rights.IsOwner)
        {
            await next();

            return;
        }

        var page = (context.ActionDescriptor as CompiledPageActionDescriptor)?.ViewEnginePath
            ?? context.ActionDescriptor.DisplayName
            ?? "";
        if (rights.IsStopped || !MerchantPages.Needs.TryGetValue(page, out var needed) || !rights.May(needed))
        {
            context.Result = new ForbidResult();

            return;
        }

        await next();
    }
}
