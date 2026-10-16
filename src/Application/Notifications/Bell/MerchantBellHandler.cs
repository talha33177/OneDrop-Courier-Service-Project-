using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Application.Payments.MerchantPayments;
using Domain.Common;
using Domain.Delivery;
using Domain.Merchants;
using Domain.Parcels;
using Domain.Payments;

namespace Application.Notifications.Bell;

/// <summary>How a notice reads at a glance: done, worth a look, a problem, or money.</summary>
public enum NoticeTone
{
    Done,
    Attention,
    Problem,
    Money
}

/// <summary>Something that happened to the merchant's parcels, money or requests. <see cref="At"/> is UTC.</summary>
public sealed record Notice(DateTime At, NoticeTone Tone, string Icon, string Text, string Link);

/// <summary>The bell: the latest notices, newest first, and how many came after the merchant last looked.</summary>
public sealed record Bell(IReadOnlyList<Notice> Notices, int Unread)
{
    /// <summary>The newest notice's time, which is what the merchant has seen once the bell is opened.</summary>
    public long Latest => Notices.Count == 0 ? 0 : Notices[0].At.Ticks;
}

/// <summary>
/// The merchant panel's bell, worked out when asked from what the courier already keeps, so nothing new is written as
/// parcels move: parcels delivered (a line a day), partly delivered, held, refused at the door or back with the
/// merchant; problems a hub flagged; return lists to confirm; answered requests; payouts sent and requests to be paid
/// now that were refused, over the last week.
/// A moderator is told only about what their permissions show. The merchant's own (the merchant filter).
/// </summary>
public class MerchantBellHandler(IAppDbContext db, ITenantContext tenantContext, TimeProvider time)
{
    /// <summary>How far back the bell looks.</summary>
    public const int Days = 7;

    /// <summary>The most notices the bell shows.</summary>
    public const int Max = 20;

    public async Task<Bell> ForAsync(bool parcels, bool payments, DateTime? seen, CancellationToken cancellationToken = default)
    {
        var since = time.GetUtcNow().UtcDateTime.AddDays(-Days);
        List<Notice> notices = [];
        if (parcels)
        {
            notices.AddRange(await FinishedAsync(since, cancellationToken));
            notices.AddRange(await AtTheDoorAsync(since, cancellationToken));
            notices.AddRange(await FlaggedAsync(since, cancellationToken));
            notices.AddRange(await ReturnsAndRequestsAsync(since, cancellationToken));
        }

        if (payments)
        {
            notices.AddRange(await PayoutsAsync(since, cancellationToken));
        }

        var latest = notices.OrderByDescending(notice => notice.At).Take(Max).ToList();

        return new Bell(latest, latest.Count(notice => seen is null || notice.At > seen));
    }

    /// <summary>Delivered parcels as one line a day, and each parcel partly delivered or back with the merchant.</summary>
    private async Task<IEnumerable<Notice>> FinishedAsync(DateTime since, CancellationToken cancellationToken)
    {
        var tenant = tenantContext.Require();
        var finished = await db.Parcels
            .Where(p => p.ClosedOn >= since &&
                (p.Status == ParcelStatus.Delivered || p.Status == ParcelStatus.PartlyDelivered || p.Status == ParcelStatus.Returned))
            .Select(p => new { p.TrackingCode, p.Status, ClosedOn = p.ClosedOn!.Value, p.CodAmount, p.CollectedAmount })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var delivered = finished
            .Where(p => p.Status == ParcelStatus.Delivered)
            .GroupBy(p => tenant.Today(p.ClosedOn))
            .Select(day => new Notice(
                day.Max(p => p.ClosedOn),
                NoticeTone.Done,
                "check-circle",
                $"{day.Count()} parcel{(day.Count() == 1 ? "" : "s")} delivered on {day.Key:ddd d MMM}, ৳{day.Sum(p => p.CollectedAmount ?? 0):N0} collected",
                $"/Merchant/Parcels?tab=Delivered&from={day.Key:yyyy-MM-dd}&to={day.Key:yyyy-MM-dd}"));
        var others = finished
            .Where(p => p.Status != ParcelStatus.Delivered)
            .Select(p => p.Status == ParcelStatus.PartlyDelivered
                ? new Notice(p.ClosedOn, NoticeTone.Attention, "check", $"{p.TrackingCode} was partly delivered: ৳{p.CollectedAmount ?? 0:N0} of ৳{p.CodAmount:N0} collected", $"/Merchant/Parcel/{p.TrackingCode}")
                : new Notice(p.ClosedOn, NoticeTone.Problem, "return", $"{p.TrackingCode} is back with you", $"/Merchant/Parcel/{p.TrackingCode}"));

        return delivered.Concat(others);
    }

    /// <summary>What the riders recorded at the door that the merchant should know: a hold for another day, a refusal.</summary>
    private async Task<IEnumerable<Notice>> AtTheDoorAsync(DateTime since, CancellationToken cancellationToken)
    {
        var attempts = await (
            from attempt in db.DeliveryAttempts
            join parcel in db.Parcels on attempt.ParcelId equals parcel.Id
            where attempt.CompletedOn >= since && (attempt.Outcome == AttemptOutcome.Hold || attempt.Outcome == AttemptOutcome.Refused)
            select new { parcel.TrackingCode, attempt.Outcome, CompletedOn = attempt.CompletedOn!.Value, attempt.Reason })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return attempts.Select(a => a.Outcome == AttemptOutcome.Hold
            ? new Notice(a.CompletedOn, NoticeTone.Attention, "pause", $"{a.TrackingCode} is on hold: {a.Reason}", $"/Merchant/Parcel/{a.TrackingCode}")
            : new Notice(a.CompletedOn, NoticeTone.Problem, "return", $"{a.TrackingCode} was refused at the door: {a.Reason}. It is coming back to you", $"/Merchant/Parcel/{a.TrackingCode}"));
    }

    /// <summary>Problems a hub flagged, still open.</summary>
    private async Task<IEnumerable<Notice>> FlaggedAsync(DateTime since, CancellationToken cancellationToken)
    {
        var flagged = await db.Parcels
            .Where(p => p.Issue != null && p.IssueRaisedOn >= since)
            .Select(p => new { p.TrackingCode, Issue = p.Issue!.Value, p.IssueNote, RaisedOn = p.IssueRaisedOn!.Value })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return flagged.Select(p => new Notice(
            p.RaisedOn,
            p.Issue == ParcelIssue.Exceptional ? NoticeTone.Problem : NoticeTone.Attention,
            "flag",
            $"{p.TrackingCode} is {p.Issue.DisplayName().ToLowerInvariant()}: {p.IssueNote}",
            $"/Merchant/Parcel/{p.TrackingCode}"));
    }

    /// <summary>Return lists a rider handed over for the merchant to confirm, and the courier's answers to its requests.</summary>
    private async Task<IEnumerable<Notice>> ReturnsAndRequestsAsync(DateTime since, CancellationToken cancellationToken)
    {
        var lists = await db.ReturnLists
            .Where(l => l.Status == ReturnListStatus.HandedOver && l.HandedOverOn >= since)
            .Select(l => new { l.Number, HandedOverOn = l.HandedOverOn!.Value })
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var answers = await (
            from request in db.ParcelRequests
            join parcel in db.Parcels on request.ParcelId equals parcel.Id
            where request.Status != ParcelRequestStatus.Open && request.AnsweredOn >= since
            select new { parcel.TrackingCode, request.Status, AnsweredOn = request.AnsweredOn!.Value })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return lists
            .Select(l => new Notice(l.HandedOverOn, NoticeTone.Attention, "clipboard", $"Return list {l.Number} was handed over to you: confirm the parcels arrived", $"/Merchant/Return/{l.Number}"))
            .Concat(answers.Select(a => new Notice(
                a.AnsweredOn,
                a.Status == ParcelRequestStatus.Approved ? NoticeTone.Done : NoticeTone.Problem,
                "message",
                $"The courier {(a.Status == ParcelRequestStatus.Approved ? "approved" : "refused")} your request about {a.TrackingCode}",
                $"/Merchant/Parcel/{a.TrackingCode}")));
    }

    private async Task<IEnumerable<Notice>> PayoutsAsync(DateTime since, CancellationToken cancellationToken)
    {
        var payouts = await db.Payouts
            .Where(p => p.Status == PayoutStatus.Paid && p.PaidOn >= since)
            .Select(p => new { p.Number, p.Amount, p.Method, p.Account, PaidOn = p.PaidOn!.Value })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // A request to be paid now that the courier paid shows as its payout; one it refused, with why
        var refused = await db.PayoutRequests
            .Where(r => r.Status == PayoutRequestStatus.Refused && r.AnsweredOn >= since)
            .Select(r => new { AnsweredOn = r.AnsweredOn!.Value, r.Answer })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return payouts
            .Select(p => new Notice(
                p.PaidOn,
                NoticeTone.Money,
                "wallet",
                $"Payout {p.Number} of ৳{p.Amount:N0} was sent to {p.Method.DisplayName()} " +
                    (p.Method != PayoutMethod.Bank && PhoneNumber.Parse(p.Account) is { IsSuccess: true } phone ? phone.Value.Local : p.Account),
                $"/Merchant/Payment/{p.Number}"))
            .Concat(refused.Select(r => new Notice(
                r.AnsweredOn,
                NoticeTone.Problem,
                "message",
                $"The courier did not pay your request early: {r.Answer}",
                "/Merchant/Payments")));
    }
}
