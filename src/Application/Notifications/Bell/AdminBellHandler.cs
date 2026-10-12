using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Domain.Delivery;
using Domain.Merchants;
using Domain.Notifications;
using Domain.Parcels;
using Domain.Payments;

namespace Application.Notifications.Bell;

/// <summary>
/// The admin panel's bell: what is waiting for the courier's admin, worked out when asked from what the courier already
/// keeps. Sign-ups to approve, requests to answer, parcels a hub flagged, messages that failed, payouts the gateway
/// refused, merchants asking to be paid now, online payments the gateway held for a check and merchants owed money with
/// no payout account are listed
/// while they wait, each as one line with its count;
/// riders who handed in less cash than they collected and notes merchants left on return lists are listed for the last
/// week. A notice's time is that of the newest thing in it, so it shows as new when something joined it since the bell
/// was last opened.
/// </summary>
public class AdminBellHandler(IAppDbContext db, TimeProvider time)
{
    /// <summary>How far back the bell looks for short riders and return list notes.</summary>
    public const int Days = MerchantBellHandler.Days;

    /// <summary>The most return list notes listed one by one.</summary>
    public const int MaxNotes = 5;

    public async Task<Bell> ForAsync(DateTime? seen, CancellationToken cancellationToken = default)
    {
        var since = time.GetUtcNow().UtcDateTime.AddDays(-Days);
        List<Notice> notices = [];

        var signUps = await db.Merchants
            .Where(m => m.Status == MerchantStatus.Pending && !m.Archived)
            .GroupBy(m => 1)
            .Select(g => new { Count = g.Count(), Latest = g.Max(m => m.Created) })
            .FirstOrDefaultAsync(cancellationToken);
        if (signUps is not null)
        {
            notices.Add(new Notice(
                signUps.Latest,
                NoticeTone.Attention,
                "store",
                $"{Plural(signUps.Count, "sign-up")} waiting for your approval",
                "/Admin/Merchants?status=Pending"));
        }

        var requests = await db.ParcelRequests
            .Where(r => r.Status == ParcelRequestStatus.Open)
            .GroupBy(r => 1)
            .Select(g => new { Count = g.Count(), Latest = g.Max(r => r.Created) })
            .FirstOrDefaultAsync(cancellationToken);
        if (requests is not null)
        {
            notices.Add(new Notice(
                requests.Latest,
                NoticeTone.Attention,
                "message",
                $"{Plural(requests.Count, "merchant request")} to answer",
                "/Admin/Requests"));
        }

        var flags = await db.Parcels
            .Where(p => p.Issue != null)
            .GroupBy(p => p.Issue)
            .Select(g => new { Issue = g.Key, Count = g.Count(), Latest = g.Max(p => p.IssueRaisedOn) })
            .ToListAsync(cancellationToken);
        notices.AddRange(flags.Select(flag => flag.Issue == ParcelIssue.InReview
            ? new Notice(flag.Latest!.Value, NoticeTone.Attention, "flag", $"{Plural(flag.Count, "parcel")} flagged in review", "/Hub/Parcels?tab=InReview")
            : new Notice(flag.Latest!.Value, NoticeTone.Problem, "flag", $"{Plural(flag.Count, "parcel")} flagged exceptional", "/Hub/Parcels?tab=Exceptional")));

        var failed = await db.OutboxMessages
            .Where(m => m.Status == OutboxStatus.Failed)
            .GroupBy(m => 1)
            .Select(g => new { Count = g.Count(), Latest = g.Max(m => m.UpdatedOn) })
            .FirstOrDefaultAsync(cancellationToken);
        if (failed is not null)
        {
            notices.Add(new Notice(
                failed.Latest,
                NoticeTone.Problem,
                "x-circle",
                $"{Plural(failed.Count, "text, webhook or email", "texts, webhooks or emails")} could not be sent",
                "/Admin/Messages"));
        }

        var stuck = await db.Payouts
            .Where(p => p.Status == PayoutStatus.Pending && p.FailedAttempts > 0)
            .GroupBy(p => 1)
            .Select(g => new { Count = g.Count(), Amount = g.Sum(p => p.Amount), Latest = g.Max(p => p.LastTriedOn) })
            .FirstOrDefaultAsync(cancellationToken);
        if (stuck is not null)
        {
            notices.Add(new Notice(
                stuck.Latest!.Value,
                NoticeTone.Problem,
                "wallet",
                $"{Plural(stuck.Count, "payout")} of ৳{stuck.Amount:N0} refused by the gateway",
                "/Admin/Payouts"));
        }

        var asking = await db.PayoutRequests
            .Where(r => r.Status == PayoutRequestStatus.Open)
            .GroupBy(r => 1)
            .Select(g => new { Count = g.Count(), Latest = g.Max(r => r.Created) })
            .FirstOrDefaultAsync(cancellationToken);
        if (asking is not null)
        {
            notices.Add(new Notice(
                asking.Latest,
                NoticeTone.Attention,
                "send",
                $"{Plural(asking.Count, "merchant")} asking to be paid now",
                "/Admin/Payouts"));
        }

        // Money a merchant paid online that the gateway took but held: risky, or another amount than asked
        var held = await db.OnlinePayments
            .Where(p => p.Status == OnlinePaymentStatus.Review)
            .GroupBy(p => 1)
            .Select(g => new { Count = g.Count(), Latest = g.Max(p => p.ConfirmedOn) })
            .FirstOrDefaultAsync(cancellationToken);
        if (held is not null)
        {
            notices.Add(new Notice(
                held.Latest!.Value,
                NoticeTone.Attention,
                "flag",
                $"{Plural(held.Count, "online payment")} from merchants to check",
                "/Admin/Payouts"));
        }

        // Owed money but nowhere to send it: the merchant has no payout account yet
        var noAccount = await (
            from line in db.LedgerEntries
            join merchant in db.Merchants on line.MerchantId equals merchant.Id
            where line.PayoutId == null && merchant.PayoutMethod == null
            group line by line.MerchantId into lines
            where lines.Sum(l => l.Amount) > 0
            select new { Amount = lines.Sum(l => l.Amount), Latest = lines.Max(l => l.Created) })
            .ToListAsync(cancellationToken);
        if (noAccount.Count > 0)
        {
            notices.Add(new Notice(
                noAccount.Max(m => m.Latest),
                NoticeTone.Money,
                "wallet",
                $"{Plural(noAccount.Count, "merchant")} owed ৳{noAccount.Sum(m => m.Amount):N0} with no payout account",
                "/Admin/Payouts"));
        }

        var shortRiders = await db.DeliveryRuns
            .Where(r => r.Status == RunStatus.Closed && r.ClosedOn >= since && r.CashReceived < r.CashExpected)
            .GroupBy(r => 1)
            .Select(g => new
            {
                Riders = g.Select(r => r.RiderId).Distinct().Count(),
                Amount = g.Sum(r => r.CashExpected - r.CashReceived),
                Latest = g.Max(r => r.ClosedOn)
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (shortRiders is not null)
        {
            notices.Add(new Notice(
                shortRiders.Latest!.Value,
                NoticeTone.Problem,
                "cash",
                $"{Plural(shortRiders.Riders, "rider")} handed in ৳{shortRiders.Amount:N0} less than collected this week",
                "/Admin/Reports"));
        }

        var notes = await (
            from list in db.ReturnLists
            join merchant in db.Merchants on list.MerchantId equals merchant.Id
            join hub in db.Hubs on list.HubId equals hub.Id
            where list.Note != null && list.ConfirmedOn >= since
            orderby list.ConfirmedOn descending
            select new { list.Number, list.Note, ConfirmedOn = list.ConfirmedOn!.Value, Merchant = merchant.Name, Hub = hub.Code })
            .Take(MaxNotes)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        notices.AddRange(notes.Select(note => new Notice(
            note.ConfirmedOn,
            NoticeTone.Attention,
            "return",
            $"{note.Merchant} on {note.Number}: “{note.Note}”",
            $"/Hub/Returns?hub={note.Hub}")));

        var latest = notices.OrderByDescending(notice => notice.At).ToList();

        return new Bell(latest, latest.Count(notice => seen is null || notice.At > seen));
    }

    private static string Plural(int count, string one, string? many = null)
    {
        return count == 1 ? $"1 {one}" : $"{count:N0} {many ?? one + "s"}";
    }
}
