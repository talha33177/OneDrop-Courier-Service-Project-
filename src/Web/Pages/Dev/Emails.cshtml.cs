using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Abstractions;
using Application.Notifications.SendEmails;
using Infrastructure.Connections;
using Infrastructure.Email;

namespace Web.Pages.Dev;

/// <summary>
/// Development only: the emails the courier has sent merchants, so a demo can read them without a mailbox. With a
/// mail server configured they are really sent as well, and the button here proves the server answers.
/// </summary>
public class EmailsModel(
    EmailLog log,
    ConnectionSettings connections,
    IEmailSender sender,
    ITenantContext tenantContext,
    ITrackingLinks links,
    IWebHostEnvironment environment) : PageModel
{
    public IReadOnlyList<SentEmail> Messages { get; private set; } = [];

    public bool Live => Options.Configured;

    public string? MailServer => Options.Host;

    private EmailOptions Options => connections.Mail();

    [TempData]
    public string? Done { get; set; }

    public string? Problem { get; private set; }

    public IActionResult OnGet()
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }

        Messages = log.Recent;

        return Page();
    }

    /// <summary>Sends one email to the courier's own address, the quickest way to see the mail server works.</summary>
    public async Task<IActionResult> OnPostTestAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }

        var to = Options.From ?? "nobody@example.com";
        try
        {
            var tenant = tenantContext.Tenant;
            var brand = new EmailBrand(tenant?.Name ?? "The courier", tenant?.SupportPhone ?? "", tenant is null ? "/" : links.Page(""));
            var written = EmailLayout.Render(
                new EmailContent
                {
                    Subject = "Test email from the courier panel",
                    Preview = "The mail server works.",
                    Label = "Test email",
                    Tone = EmailTone.Good,
                    Title = "The mail server works",
                    Paragraphs = ["This is a test email from the courier panel's development tools."]
                },
                brand);
            await sender.SendAsync(written with { To = to, ToName = Options.FromName }, cancellationToken);
            Done = Live ? $"Test email sent to {to}." : "No mail server is configured, so the test email was kept here.";

            return RedirectToPage();
        }
        catch (Exception exception)
        {
            Problem = exception.Message;
            Messages = log.Recent;

            return Page();
        }
    }
}
