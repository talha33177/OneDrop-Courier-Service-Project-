using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Platform.Staff;
using Domain.Staff;

namespace Web.Authentication;

/// <summary>
/// What each page of the hub and admin panels asks of the courier's staff: any one of the permissions listed. A super
/// admin may open everything; anyone else only the pages their permissions cover. A page missing from this list is
/// the super admin's alone, so a new page is shut until it is listed on purpose. A button that changes something can
/// ask more than its page, listed as "page/handler".
/// </summary>
public static class StaffPages
{
    private const StaffPermissions AnyHubDesk = StaffPermissions.HubWork;

    public static readonly IReadOnlyDictionary<string, StaffPermissions> Needs =
        new Dictionary<string, StaffPermissions>(StringComparer.OrdinalIgnoreCase)
        {
            // The board and the chooser show every desk's work; anyone working at a hub, and operations, see them
            ["/Hub/Index"] = AnyHubDesk | StaffPermissions.Operations,
            ["/Hub/Choose"] = AnyHubDesk | StaffPermissions.Operations,

            // Finding a parcel is everyone's (customer support answers the hotline with it); changing one is a desk's
            ["/Hub/Parcels"] = StaffPermissions.None,
            ["/Hub/Parcel"] = StaffPermissions.None,
            ["/Hub/Parcel/Reweigh"] = StaffPermissions.Receive,
            ["/Hub/Parcel/Flag"] = StaffPermissions.Receive | StaffPermissions.Operations,
            ["/Hub/Parcel/ClearFlag"] = StaffPermissions.Receive | StaffPermissions.Operations,
            ["/Hub/Parcel/Return"] = StaffPermissions.Returns,
            ["/Hub/Labels"] = AnyHubDesk,

            // Each mode of the scanner is checked again on the page itself
            ["/Hub/Scan"] = StaffPermissions.Receive | StaffPermissions.Dispatch | StaffPermissions.Returns,
            ["/Hub/Pickups"] = StaffPermissions.DeliveryDesk,
            ["/Hub/Assign"] = StaffPermissions.DeliveryDesk,
            ["/Hub/RunSheet"] = StaffPermissions.DeliveryDesk | StaffPermissions.CashDesk,
            ["/Hub/Runs"] = StaffPermissions.CashDesk,
            ["/Hub/Returns"] = StaffPermissions.Returns,

            ["/Admin/Index"] = StaffPermissions.Operations,
            ["/Admin/Guide"] = StaffPermissions.None,
            ["/Admin/Stuck"] = StaffPermissions.Operations,
            ["/Admin/Messages"] = StaffPermissions.Operations,
            ["/Admin/Riders"] = StaffPermissions.Operations,
            ["/Admin/Rider"] = StaffPermissions.Operations,
            ["/Admin/Requests"] = StaffPermissions.Operations | StaffPermissions.Merchants,

            // Accounts reads a merchant to pay it; only merchant support changes the account itself
            ["/Admin/Merchants"] = StaffPermissions.Merchants | StaffPermissions.Finance,
            ["/Admin/Merchant"] = StaffPermissions.Merchants | StaffPermissions.Finance,
            ["/Admin/Merchant/Approve"] = StaffPermissions.Merchants,
            ["/Admin/Merchant/Suspend"] = StaffPermissions.Merchants,
            ["/Admin/Merchant/Reactivate"] = StaffPermissions.Merchants,
            ["/Admin/Merchant/Profile"] = StaffPermissions.Merchants,
            ["/Admin/Merchant/Payout"] = StaffPermissions.Finance,
            ["/Admin/Merchant/Hold"] = StaffPermissions.Finance,
            ["/Admin/Merchant/Release"] = StaffPermissions.Finance,
            ["/Admin/Merchant/PayNow"] = StaffPermissions.Finance,
            ["/Admin/Merchant/Adjust"] = StaffPermissions.Finance,
            ["/Admin/NewMerchant"] = StaffPermissions.Merchants,

            ["/Admin/Payouts"] = StaffPermissions.Finance,
            ["/Admin/Payout"] = StaffPermissions.Finance,
            ["/Admin/Reports"] = StaffPermissions.Finance,

            ["/Admin/Coverage"] = StaffPermissions.Setup,
            ["/Admin/Rates"] = StaffPermissions.Setup,
            ["/Admin/DeliveryTypes"] = StaffPermissions.Setup,
            ["/Admin/Vehicles"] = StaffPermissions.Setup,
            ["/Admin/Settings"] = StaffPermissions.Setup,

            // A hub manager sees only their own hub's people there; head office everyone below super admin
            ["/Admin/Staff"] = StaffPermissions.Staff
        };

    /// <summary>Where the rights of the person signed in are kept for the rest of the request.</summary>
    public const string RightsKey = "staff-rights";

    /// <summary>The signed-in staff member's rights, read by <see cref="StaffPermissionFilter"/> earlier in the request.</summary>
    public static StaffRights? Rights(HttpContext context)
    {
        return context.Items[RightsKey] as StaffRights;
    }

    /// <summary>Whether a page, by its view path ("/Hub/Index"), opens for these rights; a page not listed does not.</summary>
    public static bool MayOpen(StaffRights rights, string page)
    {
        return rights.IsSuperAdmin || (Needs.TryGetValue(page, out var needed) && rights.May(needed));
    }

    /// <summary>Whether a link ("/Admin/Merchants?status=Pending", "/Hub") opens for these rights.</summary>
    public static bool MayFollow(StaffRights rights, string href)
    {
        var path = href.Split('?', '#')[0].TrimEnd('/');

        return MayOpen(rights, path.Count(c => c == '/') == 1 ? $"{path}/Index" : path);
    }
}

/// <summary>
/// Holds the courier's staff to the pages and buttons their permissions cover. The rights are read once and left on
/// the request, so the layout and the hub pages can use them without asking again.
/// </summary>
public class StaffPermissionFilter(StaffAccess access) : IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context)
    {
        return Task.CompletedTask;
    }

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var rights = await access.CurrentAsync(context.HttpContext.RequestAborted);
        context.HttpContext.Items[StaffPages.RightsKey] = rights;
        if (rights.IsSuperAdmin)
        {
            await next();

            return;
        }

        var page = (context.ActionDescriptor as CompiledPageActionDescriptor)?.ViewEnginePath
            ?? context.ActionDescriptor.DisplayName
            ?? "";
        var handler = context.HandlerMethod?.Name;
        var allowed = StaffPages.MayOpen(rights, page) &&
            (handler is null || !StaffPages.Needs.TryGetValue($"{page}/{handler}", out var needed) || rights.May(needed));
        if (!allowed)
        {
            context.Result = new ForbidResult();

            return;
        }

        await next();
    }
}
