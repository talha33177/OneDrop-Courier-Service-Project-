using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Domain.Merchants;
using Domain.Notifications;

namespace Application.Notifications.SendEmails;

/// <summary>
/// Emails to the courier's own super admins, each to every one of them who may still sign in. Today: a shop signed up
/// and waits for approval.
/// </summary>
public class CourierEmails(IAppDbContext db, ITenantContext tenantContext, IEmailSender email, IUserAccounts accounts, ITrackingLinks links)
{
    public async Task SendAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Tenant ?? throw new InvalidOperationException("Emails need a tenant.");
        var payload = JsonSerializer.Deserialize<CourierEmailMessage>(message.Payload)
            ?? throw new InvalidOperationException($"Outbox message {message.Id} has no payload.");

        var merchant = await db.Merchants
            .IgnoreQueryFilters([QueryFilters.Merchant])
            .AsNoTracking()
            .SingleOrDefaultAsync(m => m.Id == payload.SubjectId, cancellationToken);
        if (merchant is null || merchant.Status != MerchantStatus.Pending)
        {
            // Approved (or turned away) before the email went: nothing waits any more
            return;
        }

        var content = new EmailContent
        {
            Subject = $"New sign-up waiting for approval: {merchant.Name}",
            Preview = $"{merchant.OwnerName} signed up {merchant.Name}. Check and approve it.",
            Label = "New sign-up",
            Title = $"{merchant.Name} wants to send parcels with you",
            Paragraphs = ["A new shop signed up and cannot book parcels until it is approved. Check its details, then approve it from its page."],
            Rows =
            [
                new EmailRow("Shop", merchant.Name),
                new EmailRow("Owner", merchant.OwnerName),
                new EmailRow("Phone", merchant.ContactPhone),
                .. merchant.ContactEmail is null ? Array.Empty<EmailRow>() : [new EmailRow("Email", merchant.ContactEmail)],
                new EmailRow("Address", merchant.Address)
            ],
            Button = ("Review the sign-up", links.Page($"Admin/Merchant/{merchant.Id}")),
            Small = "You get this email because you are a super admin of the courier."
        };
        var written = EmailLayout.Render(content, new EmailBrand(tenant.Name, tenant.SupportPhone, links.Page("")));

        foreach (var admin in await accounts.InRolesAsync([Roles.TenantAdmin], cancellationToken))
        {
            if (!admin.Stopped && admin.Email.Contains('@'))
            {
                await email.SendAsync(written with { To = admin.Email, ToName = admin.Name }, cancellationToken);
            }
        }
    }
}
