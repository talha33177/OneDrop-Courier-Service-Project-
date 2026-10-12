using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Domain.Delivery;
using Domain.Merchants;
using Domain.Notifications;
using Domain.Parcels;
using Domain.Payments;

namespace Application.Notifications.Bell;

/// <summary>
/// The places in the menu where something waits for the person signed in, by the menu item's address, so the menu can
/// mark them. Only work that is open counts, never what merely happened: the bell tells what happened. Each question is
/// an <c>EXISTS</c>, so the menu costs a few small reads per page.
/// </summary>
public class MenuWorkHandler(IAppDbContext db, ICurrentUser currentUser, ITenantContext tenantContext, TimeProvider time)
{
    /// <summary>
    /// For an admin: sign-ups to approve, requests to answer, parcels a hub flagged, work at the hubs (a pickup with no
    /// rider, a parcel on a hub's shelf), messages that failed, and payouts that wait for the admin: a merchant asking to
    /// be paid now, a transfer refused by the gateway, an online payment held for a check, or money owed to a merchant
    /// with no payout account.
    /// </summary>
    public async Task<IReadOnlySet<string>> AdminAsync(CancellationToken cancellationToken = default)
    {
        HashSet<string> waiting = [];
        if (await db.Merchants.AnyAsync(m => m.Status == MerchantStatus.Pending && !m.Archived, cancellationToken))
        {
            waiting.Add("/Admin/Merchants");
        }

        if (await db.ParcelRequests.AnyAsync(r => r.Status == ParcelRequestStatus.Open, cancellationToken))
        {
            waiting.Add("/Admin/Requests");
        }

        if (await db.Parcels.AnyAsync(p => p.Issue != null, cancellationToken))
        {
            waiting.Add("/Hub/Parcels");
        }

        // Today's pickups: tomorrow's list (parcels booked after the cut-off) is not work yet
        var today = tenantContext.Require().Today(time.GetUtcNow().UtcDateTime);
        if (await db.PickupRequests.AnyAsync(r => r.Status == PickupStatus.Scheduled && r.PickupDate <= today, cancellationToken) ||
            await db.Parcels.AnyAsync(p => p.CurrentHubId != null, cancellationToken))
        {
            waiting.Add("/Hub");
        }

        if (await db.OutboxMessages.AnyAsync(m => m.Status == OutboxStatus.Failed, cancellationToken))
        {
            waiting.Add("/Admin/Messages");
        }

        var owedWithNoAccount =
            from line in db.LedgerEntries
            join merchant in db.Merchants on line.MerchantId equals merchant.Id
            where line.PayoutId == null && merchant.PayoutMethod == null
            group line by line.MerchantId into lines
            where lines.Sum(l => l.Amount) > 0
            select lines.Key;
        if (await db.PayoutRequests.AnyAsync(r => r.Status == PayoutRequestStatus.Open, cancellationToken) ||
            await db.Payouts.AnyAsync(p => p.Status == PayoutStatus.Pending && p.FailedAttempts > 0, cancellationToken) ||
            await db.OnlinePayments.AnyAsync(p => p.Status == OnlinePaymentStatus.Review, cancellationToken) ||
            await owedWithNoAccount.AnyAsync(cancellationToken))
        {
            waiting.Add("/Admin/Payouts");
        }

        return waiting;
    }

    /// <summary>
    /// For the business being worked in: return lists handed over and not yet confirmed, and no payout account to be
    /// paid to. Pickups need nothing from the merchant: every parcel booked goes with the day's pickup by itself.
    /// </summary>
    public async Task<IReadOnlySet<string>> MerchantAsync(CancellationToken cancellationToken = default)
    {
        HashSet<string> waiting = [];
        var merchant = await db.Merchants
            .Where(m => m.Id == currentUser.MerchantId)
            .Select(m => new { HasAccount = m.PayoutMethod != null && m.PayoutAccount != null })
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (merchant is null)
        {
            return waiting;
        }

        if (await db.ReturnLists.AnyAsync(l => l.Status == ReturnListStatus.HandedOver, cancellationToken))
        {
            waiting.Add("/Merchant/Returns");
        }

        if (!merchant.HasAccount)
        {
            waiting.Add("/Merchant/Settings");
        }

        return waiting;
    }

    /// <summary>For a rider: parcels still to take to the door or return lists to hand back, and pickups to collect.</summary>
    public async Task<IReadOnlySet<string>> RiderAsync(CancellationToken cancellationToken = default)
    {
        HashSet<string> waiting = [];
        var userId = currentUser.UserId;
        var rider = userId is null
            ? null
            : await db.Riders.Where(r => r.UserId == userId && !r.Archived).Select(r => (long?)r.Id).SingleOrDefaultAsync(cancellationToken);
        if (rider is null)
        {
            return waiting;
        }

        if (await db.DeliveryAttempts.AnyAsync(a => a.RiderId == rider && a.Outcome == null, cancellationToken) ||
            await db.ReturnLists.AnyAsync(l => l.RiderId == rider && l.Status == ReturnListStatus.Out, cancellationToken))
        {
            waiting.Add("/Rider");
        }

        if (await db.PickupRequests.AnyAsync(r => r.RiderId == rider && r.Status == PickupStatus.Assigned, cancellationToken))
        {
            waiting.Add("/Rider/Pickups");
        }

        return waiting;
    }
}
