using System.Security.Claims;
using Application.Common;
using Application.Merchants.Moderators;
using Application.Platform.Staff;
using Web.Authentication;

namespace Web.Display;

/// <summary>
/// One place in the menu: where it goes, what it is called, its icon, and the pages that count as being there (a
/// parcel's page lights up "All parcels"). The address doubles as the item's key in a person's favourites.
/// </summary>
public sealed record MenuItem(string Href, string Label, string Icon, params string[] Pages);

public sealed record MenuSection(string? Label, IReadOnlyList<MenuItem> Items);

/// <summary>
/// The panel's menu for whoever is signed in, and the favourites shown above every page. Favourites are kept in a
/// cookie per login, so the bar is drawn with the page and the same browser can hold several people's choices.
/// </summary>
public static class Menu
{
    public const int MaxFavourites = 8;

    private static readonly MenuSection[] MerchantMenu =
    [
        new(null,
        [
            new("/Merchant", "Dashboard", "dashboard", "/Merchant/Index"),
            new("/Merchant/Stats", "Stats", "chart", "/Merchant/Stats")
        ]),
        new("Businesses",
        [
            // Adding a business starts from this page, so it lights up for the add page too
            new("/Merchant/Businesses", "Manage businesses", "layers", "/Merchant/Businesses", "/Merchant/NewBusiness")
        ]),
        new("Parcels",
        [
            new("/Merchant/NewParcel", "Book a parcel", "plus", "/Merchant/NewParcel"),
            new("/Merchant/Parcels", "All parcels", "package", "/Merchant/Parcels", "/Merchant/Parcel", "/Merchant/EditParcel"),
            new("/Merchant/ByDate", "By date", "calendar", "/Merchant/ByDate"),
            new("/Merchant/BulkUpload", "Bulk upload", "upload", "/Merchant/BulkUpload"),
            new("/Merchant/Pickups", "Pickups", "truck", "/Merchant/Pickups"),
            new("/Merchant/Returns", "Returns", "return", "/Merchant/Returns", "/Merchant/Return"),
            new("/Merchant/Requests", "Requests", "message", "/Merchant/Requests"),
            new("/Merchant/Labels", "Print labels", "printer", "/Merchant/Labels"),
            new("/Merchant/Export", "Export", "download", "/Merchant/Export")
        ]),
        new("Money and tools",
        [
            new("/Merchant/Payments", "Payments", "wallet", "/Merchant/Payments", "/Merchant/Payment"),
            new("/Merchant/FraudCheck", "Fraud check", "shield", "/Merchant/FraudCheck"),
            new("/Merchant/Pricing", "Rates and calculator", "tag", "/Merchant/Pricing")
        ]),
        new("Account",
        [
            new("/Merchant/Settings", "Settings", "settings", "/Merchant/Settings"),
            new("/Merchant/Moderators", "Moderators", "users", "/Merchant/Moderators"),
            new("/Merchant/ApiKeys", "API keys", "key", "/Merchant/ApiKeys"),
            new("/Merchant/Webhook", "Webhooks", "zap", "/Merchant/Webhook")
        ])
    ];

    private static readonly MenuSection[] AdminMenu =
    [
        // In the order an admin uses them: the day's work first, then money, people, and what is set up once
        new(null,
        [
            new("/Admin", "Dashboard", "dashboard", "/Admin/Index"),
            new("/Admin/Guide", "How it works", "help", "/Admin/Guide")
        ]),
        new("Every day",
        [
            new("/Hub", "Hub operations", "warehouse", "/Hub/Index", "/Hub/Choose", "/Hub/Scan", "/Hub/Assign", "/Hub/Runs", "/Hub/RunSheet", "/Hub/Pickups", "/Hub/Returns", "/Hub/Labels", "/Hub/Bags", "/Hub/Bag", "/Hub/Cash"),
            new("/Hub/Parcels", "Parcels", "package", "/Hub/Parcels", "/Hub/Parcel"),
            new("/Admin/Stuck", "Stuck parcels", "clock", "/Admin/Stuck"),
            new("/Admin/Requests", "Requests", "message", "/Admin/Requests"),
            new("/Admin/Messages", "Failed messages", "x-circle", "/Admin/Messages")
        ]),
        new("Money",
        [
            new("/Admin/Payouts", "Merchant payouts", "wallet", "/Admin/Payouts", "/Admin/Payout"),
            new("/Admin/Cash", "Hub cash", "bank", "/Admin/Cash"),
            new("/Admin/Reports", "Reports", "chart", "/Admin/Reports")
        ]),
        new("People",
        [
            new("/Admin/Merchants", "Merchants", "store", "/Admin/Merchants", "/Admin/Merchant", "/Admin/NewMerchant"),
            new("/Admin/Riders", "Riders", "bike", "/Admin/Riders", "/Admin/Rider"),
            new("/Admin/Staff", "Staff", "users", "/Admin/Staff")
        ]),
        new("Setup",
        [
            new("/Admin/Coverage", "Coverage", "globe", "/Admin/Coverage"),
            new("/Admin/Rates", "Delivery rates", "tag", "/Admin/Rates"),
            new("/Admin/DeliveryTypes", "Delivery types", "zap", "/Admin/DeliveryTypes"),
            new("/Admin/Vehicles", "Vehicles", "truck", "/Admin/Vehicles"),
            new("/Admin/Settings", "Courier settings", "settings", "/Admin/Settings"),
            new("/Admin/Connections", "Email and payments", "lock", "/Admin/Connections")
        ])
    ];

    private static readonly MenuSection[] HubMenu =
    [
        new("Hub",
        [
            new("/Hub", "Hub board", "dashboard", "/Hub/Index"),
            new("/Hub/Scan", "Scan parcels", "scan", "/Hub/Scan"),
            new("/Hub/Pickups", "Today's pickups", "truck", "/Hub/Pickups"),
            new("/Hub/Assign", "Assign to riders", "send", "/Hub/Assign"),
            new("/Hub/Bags", "Bags between hubs", "bag", "/Hub/Bags", "/Hub/Bag"),
            new("/Hub/Runs", "Rider closing", "cash", "/Hub/Runs"),
            new("/Hub/Cash", "Cash and deposits", "bank", "/Hub/Cash"),
            new("/Hub/Returns", "Returns to merchants", "return", "/Hub/Returns")
        ]),
        new("Find", [new("/Hub/Parcels", "Parcels", "search", "/Hub/Parcels", "/Hub/Parcel")])
    ];

    private static readonly MenuSection[] RiderMenu =
    [
        new(null,
        [
            new("/Rider", "My deliveries", "bike", "/Rider/Index", "/Rider/Delivery"),
            new("/Rider/Pickups", "My pickups", "truck", "/Rider/Pickups", "/Rider/Pickup")
        ])
    ];

    private static readonly MenuSection[] PlatformMenu =
    [
        new(null,
        [
            new("/Platform/Tenants", "Couriers", "layers", "/Platform/Tenants"),
            new("/jobs", "Background jobs", "clock")
        ])
    ];

    /// <summary>
    /// The menu of whoever is signed in. <paramref name="rights"/> is the merchant account's: a moderator is shown
    /// only what their owner ticked. <paramref name="staff"/> is the courier's staff member's: each is shown only the
    /// pages their job opens. So the menu never offers a page that would be refused.
    /// </summary>
    public static IReadOnlyList<MenuSection> For(ClaimsPrincipal user, MerchantRights? rights = null, StaffRights? staff = null)
    {
        // A super admin also works the hubs, so the admin's menu already holds them
        var hubs = user.IsInRole(Roles.TenantAdmin) ? AdminMenu : user.IsInRole(Roles.HubStaff) ? Staffed(staff) : [];
        var merchant = rights is null || rights.IsOwner
            ? MerchantMenu
            : Allowed(MerchantMenu, page => MerchantPages.Needs.TryGetValue(page, out var needed) && rights.May(needed));

        return
        [
            .. user.IsInRole(Roles.Merchant) ? merchant : [],
            .. hubs,
            .. user.IsInRole(Roles.Rider) ? RiderMenu : [],
            .. user.IsInRole(Roles.PlatformAdmin) ? PlatformMenu : []
        ];
    }

    /// <summary>
    /// A staff member's menu: head office works from the admin's menu, a hub's own staff from the hub's, with their
    /// hub's people when they manage them; either way only the pages their permissions open.
    /// </summary>
    private static IReadOnlyList<MenuSection> Staffed(StaffRights? staff)
    {
        if (staff is null)
        {
            return [];
        }

        IReadOnlyList<MenuSection> menu = staff.AtHeadOffice
            ? AdminMenu
            : [.. HubMenu, new("Your hub", [new("/Admin/Staff", "Hub staff", "users", "/Admin/Staff")])];

        return Allowed(menu, page => StaffPages.MayOpen(staff, page));
    }

    /// <summary>The menu with only the items whose page <paramref name="opens"/>; a section left empty goes.</summary>
    private static IReadOnlyList<MenuSection> Allowed(IReadOnlyList<MenuSection> menu, Func<string, bool> opens)
    {
        return
        [
            .. menu
                .Select(section => section with { Items = [.. section.Items.Where(item => opens(item.Pages[0]))] })
                .Where(section => section.Items.Count > 0)
        ];
    }

    /// <summary>Whether this person gets the favourites bar: the panels worked at a desk, not the rider's phone.</summary>
    public static bool HasFavourites(ClaimsPrincipal user)
    {
        return user.IsInRole(Roles.Merchant) || user.IsInRole(Roles.TenantAdmin) || user.IsInRole(Roles.HubStaff);
    }

    public static string CookieName(ClaimsPrincipal user)
    {
        return $"favourites-{user.FindFirstValue(ClaimTypes.NameIdentifier)}";
    }

    /// <summary>
    /// The person's favourites in the order they were added: from their cookie, or the places a newcomer to their role
    /// goes most until they choose. Anything no longer in their menu is dropped.
    /// </summary>
    public static IReadOnlyList<MenuItem> Favourites(
        ClaimsPrincipal user,
        IRequestCookieCollection cookies,
        MerchantRights? rights = null,
        StaffRights? staff = null)
    {
        var items = For(user, rights, staff).SelectMany(section => section.Items).DistinctBy(item => item.Href).ToDictionary(item => item.Href);
        var chosen = cookies.TryGetValue(CookieName(user), out var saved)
            ? Uri.UnescapeDataString(saved).Split(',', StringSplitOptions.RemoveEmptyEntries)
            : Defaults(user);

        return [.. chosen.Distinct().Where(items.ContainsKey).Select(href => items[href]).Take(MaxFavourites)];
    }

    /// <summary>Whether the page being shown is this item's.</summary>
    public static bool IsCurrent(MenuItem item, string page)
    {
        return item.Pages.Contains(page);
    }

    private static string[] Defaults(ClaimsPrincipal user)
    {
        if (user.IsInRole(Roles.Merchant))
        {
            return ["/Merchant", "/Merchant/NewParcel", "/Merchant/Parcels", "/Merchant/Pickups", "/Merchant/Payments"];
        }

        return user.IsInRole(Roles.TenantAdmin)
            ? ["/Admin", "/Hub/Parcels", "/Hub", "/Admin/Merchants", "/Admin/Payouts"]
            : ["/Hub", "/Hub/Scan", "/Hub/Assign", "/Hub/Runs", "/Admin", "/Hub/Parcels", "/Admin/Payouts", "/Admin/Merchants"];
    }
}
