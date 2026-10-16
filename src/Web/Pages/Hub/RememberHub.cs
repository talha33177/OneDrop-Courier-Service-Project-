using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Domain.Staff;
using Web.Authentication;

namespace Web.Pages.Hub;

/// <summary>
/// Remembers the hub staff chose, so every hub page opens on it without asking again; before a hub is chosen, a hub
/// page sends them to choose one (<see cref="ChooseModel"/>). Only a preference: every hub page still checks the hub
/// is this courier's, and the cookie is only written after a page with that hub has rendered. Host-only, like the
/// sign-in cookie, so it stays with its courier; signing in or out forgets it.
/// The board can also show every hub at once (<see cref="AllHubs"/>, never a hub code), which is where the courier's
/// admins start: they look after every hub, and real hubs all have work waiting. A page that needs one hub (scan,
/// assign, closing) still sends them to choose.
/// </summary>
public sealed class RememberHub : IAsyncPageFilter
{
    public const string Cookie = "hub";

    public const string AllHubs = "*";

    /// <summary>
    /// The hub the request names, else the one remembered, or null. A hub's own staff always have one: theirs unless
    /// the request names another, which their hub list then does not find.
    /// </summary>
    public static string? Of(HttpContext context)
    {
        var hub = Asked(context);
        if (hub.Length is > 0 and <= 10 && hub.All(char.IsLetterOrDigit))
        {
            return hub.ToUpperInvariant();
        }

        return StaffPages.Rights(context) is { AtHeadOffice: false, HubCode: { } own } ? own : null;
    }

    /// <summary>
    /// Whether every hub was asked for or remembered, or someone who looks after every hub (a super admin, operations)
    /// has not chosen yet. Head office's relief staff choose the hub they are at today; a hub's own staff work at
    /// theirs and never see every hub.
    /// </summary>
    public static bool WantsAll(HttpContext context)
    {
        var rights = StaffPages.Rights(context);
        if (rights is null || !rights.AtHeadOffice)
        {
            return false;
        }

        var hub = Asked(context);

        return hub == AllHubs || (hub.Length == 0 && rights.May(StaffPermissions.Operations));
    }

    /// <summary>The hub named in the request, else the remembered one; a hub's own staff remember nothing.</summary>
    private static string Asked(HttpContext context)
    {
        var hub = context.Request.Query["hub"].ToString();
        if (hub.Length > 0 || StaffPages.Rights(context) is { AtHeadOffice: false })
        {
            return hub;
        }

        return context.Request.Cookies[Cookie] ?? "";
    }

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context)
    {
        return Task.CompletedTask;
    }

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (HttpMethods.IsGet(context.HttpContext.Request.Method) && context.HandlerInstance is HubPage page &&
            Of(context.HttpContext) is null && !(page.OffersAllHubs && WantsAll(context.HttpContext)))
        {
            context.Result = new RedirectToPageResult("/Hub/Choose", new { next = context.ActionDescriptor.ViewEnginePath.Split('/')[^1] });

            return;
        }

        var executed = await next();
        var hub = context.HttpContext.Request.Query["hub"].ToString();
        if (executed.Result is PageResult && (hub == AllHubs || (hub.Length is > 0 and <= 10 && hub.All(char.IsLetterOrDigit))))
        {
            context.HttpContext.Response.Cookies.Append(Cookie, hub.ToUpperInvariant(), new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromDays(30)
            });
        }
    }
}
