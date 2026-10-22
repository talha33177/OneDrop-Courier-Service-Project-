using System.Text.Json;
using Application.Abstractions;
using Domain.Notifications;

namespace Application.Notifications.SendEmails;

/// <summary>
/// Emails a login about itself: a link to choose a new password, or the news that someone made it a login. The link
/// is made when the email is sent, so no token is ever stored; it works once, and for a day. A stopped login gets
/// nothing.
/// </summary>
public class AccountEmails(ITenantContext tenantContext, IEmailSender email, IUserAccounts accounts, ITrackingLinks links)
{
    public async Task SendAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Tenant ?? throw new InvalidOperationException("Emails need a tenant.");
        var payload = JsonSerializer.Deserialize<AccountEmailMessage>(message.Payload)
            ?? throw new InvalidOperationException($"Outbox message {message.Id} has no payload.");

        var login = await accounts.FindAsync(payload.UserId, cancellationToken);
        if (login is null || login.Stopped)
        {
            return;
        }

        var code = await accounts.PasswordLinkCodeAsync(login.UserId, cancellationToken);
        var choose = links.Page($"Account/ResetPassword?c={code}");
        var content = payload.Kind == AccountEmailKind.PasswordReset
            ? new EmailContent
            {
                Subject = $"Choose a new password for {tenant.Name}",
                Preview = "Use the link within a day to choose a new password.",
                Label = "Password reset",
                Title = "Choose a new password",
                Greeting = $"Hello {login.Name},",
                Paragraphs = [$"Someone asked for a new password for {login.Email} at {tenant.Name}. If it was you, choose a new one with the button below."],
                Button = ("Choose a new password", choose),
                Small = "The link works once, for one day. If you did not ask for it, ignore this email: your password stays as it is."
            }
            : new EmailContent
            {
                Subject = $"You have a login at {tenant.Name}",
                Preview = "Sign in with this email address. Choose your own password with the link.",
                Label = "Welcome",
                Title = $"Welcome to {tenant.Name}",
                Greeting = $"Hello {login.Name},",
                Paragraphs =
                [
                    $"A login was made for you at {tenant.Name}. You sign in with this email address, {login.Email}.",
                    "The person who added you has a first password to hand you. You can also choose your own password straight away with the button below."
                ],
                Button = ("Choose your password", choose),
                Small = $"The link works once, for one day. Afterwards sign in at {links.Page("Account/Login")}, and use \"Forgot your password?\" there whenever you need a new link."
            };

        var written = EmailLayout.Render(content, new EmailBrand(tenant.Name, tenant.SupportPhone, links.Page("")));
        await email.SendAsync(written with { To = login.Email, ToName = login.Name }, cancellationToken);
    }
}
