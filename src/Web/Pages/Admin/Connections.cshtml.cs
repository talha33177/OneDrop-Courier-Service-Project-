using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Abstractions;
using Application.Common;
using Application.Platform.Connections;
using Domain.Notifications;

namespace Web.Pages.Admin;

/// <summary>
/// The courier's mail server and SSLCommerz store, kept by its super admins alone (the page is on no staff list).
/// Passwords are never shown back: an empty box keeps the one saved.
/// </summary>
public class ConnectionsModel(ConnectionsHandler connections, ITenantContext tenantContext) : PageModel
{
    public ConnectionsView View { get; private set; } = null!;

    public string CourierName => tenantContext.Require().Name;

    /// <summary>The address a test email goes to: the super admin's own.</summary>
    public string TestTo => User.FindFirstValue(ClaimTypes.Email) ?? User.Identity?.Name ?? "";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        View = await connections.GetAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostMailAsync(
        string? host,
        int port,
        string? userName,
        string? password,
        string? fromAddress,
        string? fromName,
        bool isEnabled,
        bool useSsl,
        bool allowSelfSigned,
        CancellationToken cancellationToken)
    {
        var saved = await connections.SaveMailAsync(
            new MailServerDetails(host, port, userName, fromAddress, fromName, isEnabled, useSsl, allowSelfSigned),
            password,
            cancellationToken);
        TempData[saved.IsSuccess ? "Done" : "Problem"] = saved.IsSuccess
            ? isEnabled ? "Email settings saved. Press Test connection or send a test email to check them." : "Email settings saved, and switched off: no emails are sent."
            : saved.Error!.Message;

        return RedirectToPage();
    }

    /// <summary>Tries the server as typed, saving nothing; answers the page's script with what the server said.</summary>
    public async Task<IActionResult> OnPostCheckMailAsync(
        string? host,
        int port,
        string? userName,
        string? password,
        string? fromAddress,
        bool useSsl,
        bool allowSelfSigned,
        CancellationToken cancellationToken)
    {
        var checkedServer = await connections.CheckMailAsync(
            new MailServerDetails(host, port, userName, fromAddress, null, true, useSsl, allowSelfSigned),
            password,
            cancellationToken);

        return new JsonResult(new
        {
            ok = checkedServer.IsSuccess,
            message = checkedServer.IsSuccess ? checkedServer.Value : checkedServer.Error!.Message
        });
    }

    public async Task<IActionResult> OnPostRemoveMailPasswordAsync(CancellationToken cancellationToken)
    {
        var removed = await connections.RemoveMailPasswordAsync(cancellationToken);
        TempData[removed.IsSuccess ? "Done" : "Problem"] = removed.IsSuccess
            ? "The password is removed. The server is tried without a login until you enter one."
            : removed.Error!.Message;

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveMailAsync(CancellationToken cancellationToken)
    {
        await connections.RemoveMailAsync(cancellationToken);
        TempData["Done"] = "Mail server removed.";

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestMailAsync(string? to, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(to))
        {
            TempData["Problem"] = "Enter the address to send the test email to.";

            return RedirectToPage();
        }

        var tenant = tenantContext.Require();
        var sent = await connections.SendTestAsync(to.Trim(), tenant.Name, tenant.SupportPhone, cancellationToken);
        TempData[sent.IsSuccess ? "Done" : "Problem"] = sent.IsSuccess ? $"Test email sent to {to.Trim()}. Check that inbox." : sent.Error!.Message;

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostStoreAsync(
        string? sandboxStoreId,
        string? sandboxPassword,
        string? liveStoreId,
        string? livePassword,
        bool isLive,
        CancellationToken cancellationToken)
    {
        var saved = await connections.SaveStoreAsync(
            new StoreDetails(sandboxStoreId, sandboxPassword, liveStoreId, livePassword, isLive),
            cancellationToken);
        TempData[saved.IsSuccess ? "Done" : "Problem"] = saved.IsSuccess
            ? isLive ? "Stores saved. Online payments use the live store: real money." : "Stores saved. Online payments use the sandbox store: test money."
            : saved.Error!.Message;

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveStoreAsync(CancellationToken cancellationToken)
    {
        await connections.RemoveStoreAsync(cancellationToken);
        TempData["Done"] = "Store removed.";

        return RedirectToPage();
    }
}
