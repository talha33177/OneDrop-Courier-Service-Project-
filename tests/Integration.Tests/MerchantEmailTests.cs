using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Application.Notifications;
using Application.Notifications.SendEmails;
using Application.Payments.RunPayouts;
using Domain.Notifications;
using Domain.Parcels;
using Domain.Payments;
using Infrastructure.Email;
using Infrastructure.Persistence;

namespace Integration.Tests;

/// <summary>The merchant is emailed when its payout has been sent, through the outbox like every other message.</summary>
[Collection("Emails")]
public class MerchantEmailTests(WebAppFactory factory) : AppTests(factory)
{
    [Fact]
    public async Task A_payout_that_has_left_emails_the_merchant_once_with_what_it_is_made_of()
    {
        WebAppFactory.RequireDatabase();
        await ClearEmailBacklogAsync();
        var shop = await NewMerchantAsync();
        var code = await BookAsync(shop.ApiKey, area: "Pallabi", cod: 1000);
        await using (var scope = await ScopeAsync("onedrop"))
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var parcel = await db.Parcels.SingleAsync(p => p.TrackingCode == code, Cancel);
            var rider = await db.Riders.Where(r => r.HubId == parcel.PickupHubId).Select(r => r.Id).FirstAsync(Cancel);
            parcel.ReceiveAt(parcel.PickupHubId, Today);
            parcel.AssignTo(rider, parcel.CurrentHubId!.Value);
            Assert.True(parcel.Deliver(1000, null, DateTime.UtcNow).IsSuccess);
            db.LedgerEntries.AddRange(LedgerEntry.For(parcel, Today));
            await db.SaveChangesAsync(Cancel);
        }

        var tomorrow = DateTime.UtcNow.AddDays(1);
        await using (var scope = await ScopeAsync("onedrop"))
        {
            var job = ActivatorUtilities.CreateInstance<PayoutsJob>(scope.ServiceProvider, (TimeProvider)new FakeTimeProvider(tomorrow));
            await job.PayAsync(Cancel);
        }

        var payout = await QueryAsync("onedrop", db => db.Payouts.SingleAsync(p => p.MerchantId == shop.Id, Cancel));
        var queued = await QueryAsync("onedrop", db => db.OutboxMessages
            .Where(m => m.Type == nameof(MerchantEmailMessage) && m.Payload.Contains($"\"Kind\":1,\"SubjectId\":{payout.Id}}}"))
            .ToListAsync(Cancel));
        Assert.Single(queued);

        await SendEmailsAsync("onedrop");
        var sent = Factory.Services.GetRequiredService<EmailLog>().Recent
            .Where(email => email.Subject.Contains(payout.Number, StringComparison.Ordinal))
            .ToList();
        var email = Assert.Single(sent);
        Assert.Equal(shop.Email, email.To);
        Assert.Contains("৳930", email.Subject);
        Assert.Contains("Cash collected for you: ৳1,000", email.Text);
        Assert.Contains("Our charges: −৳70", email.Text);
        Assert.Contains($"/Merchant/Payment/{payout.Number}", email.Text);

        // The outbox row is done, so a second run of the job sends nothing again
        Assert.Equal(
            OutboxStatus.Sent,
            await QueryAsync("onedrop", db => db.OutboxMessages.Where(m => m.Id == queued[0].Id).Select(m => m.Status).SingleAsync(Cancel)));
        await SendEmailsAsync("onedrop");
        Assert.Single(
            Factory.Services.GetRequiredService<EmailLog>().Recent,
            e => e.Subject.Contains(payout.Number, StringComparison.Ordinal));
    }
}
