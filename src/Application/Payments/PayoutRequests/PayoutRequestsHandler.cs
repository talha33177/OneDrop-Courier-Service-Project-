using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Application.Payments.MerchantPayments;
using Application.Payments.RunPayouts;
using Domain.Common;
using Domain.Delivery;
using Domain.Parcels;
using Domain.Payments;

namespace Application.Payments.PayoutRequests;

/// <summary>A merchant's request to be paid now, as its Payments page shows it. Times are the tenant's.</summary>
public sealed record PayoutRequestRow(
    long Id,
    decimal AmountAsked,
    PayoutRequestStatus Status,
    string? Answer,
    DateTime Created,
    DateTime? AnsweredOn,
    string? PayoutNumber);

/// <summary>The merchant's open request; its latest one when the courier refused it in the last week; and its latest ones.</summary>
public sealed record MerchantPayoutRequests(PayoutRequestRow? Open, PayoutRequestRow? Refused, IReadOnlyList<PayoutRequestRow> Recent);

/// <summary>
/// An open request with what the admin needs to decide: the balance now and how much of it is today's, how much of the
/// cash in that balance is still with riders whose run is not closed (collected at the door, not yet handed in), and
/// the parcels still on their way or coming back, with the delivery and return charges the merchant would owe if they
/// all came back.
/// </summary>
public sealed record PayoutRequestReview(
    long Id,
    long MerchantId,
    string Merchant,
    DateTime Created,
    decimal AmountAsked,
    decimal Balance,
    decimal Today,
    decimal WithRiders,
    int OnTheirWay,
    decimal ChargesIfReturned,
    string? PayoutAccount,
    string? PayoutHold)
{
    /// <summary>What would be left if the parcels on their way all came back: below ৳0 the merchant would owe the courier.</summary>
    public decimal AfterReturns => Balance - ChargesIfReturned;
}

/// <summary>
/// Paying a merchant early is the courier's decision, because today's cash may still be with a rider and parcels on
/// their way may come back and be charged. The merchant asks (<see cref="AskAsync"/>), once at a time, and may take it
/// back; the courier's admin pays the balance at once through the same payout the daily run makes
/// (<see cref="PayAsync"/>) or refuses with a reason the merchant reads. The hourly payout of everything up to yesterday
/// carries on meanwhile.
/// </summary>
public class PayoutRequestsHandler(
    IAppDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    PayoutsJob payouts,
    TimeProvider time)
{
    public static readonly Error NotFound = Error.NotFound("payoutRequest.notFound", "That request was not found.");

    private static readonly Error AlreadyOpen = Error.Conflict(
        "payoutRequest.open",
        "You have asked already. The courier is checking it; you can take it back while it waits.");

    /// <summary>The merchant's open request, and its latest ones.</summary>
    public async Task<MerchantPayoutRequests> MineAsync(CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var rows = await (
            from request in db.PayoutRequests
            join payout in db.Payouts on request.PayoutId equals (long?)payout.Id into paid
            from payout in paid.DefaultIfEmpty()
            orderby request.Id descending
            select new PayoutRequestRow(
                request.Id,
                request.AmountAsked,
                request.Status,
                request.Answer,
                request.Created,
                request.AnsweredOn,
                payout == null ? null : payout.Number))
            .Take(5)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var weekAgo = time.GetUtcNow().UtcDateTime.AddDays(-7);
        var refused = rows.FirstOrDefault() is { Status: PayoutRequestStatus.Refused } latest && latest.AnsweredOn > weekAgo ? latest.Id : (long?)null;
        var local = rows
            .Select(r => r with { Created = tenant.Local(r.Created), AnsweredOn = r.AnsweredOn is { } on ? tenant.Local(on) : null })
            .ToList();

        return new MerchantPayoutRequests(
            local.FirstOrDefault(r => r.Status == PayoutRequestStatus.Open),
            local.FirstOrDefault(r => r.Id == refused),
            local);
    }

    /// <summary>The merchant signed in asks to be paid its balance now.</summary>
    public async Task<Result> AskAsync(CancellationToken cancellationToken = default)
    {
        var merchantId = currentUser.MerchantId ?? throw new InvalidOperationException("A merchant asks to be paid.");
        if (await db.PayoutRequests.AnyAsync(r => r.MerchantId == merchantId && r.Status == PayoutRequestStatus.Open, cancellationToken))
        {
            return AlreadyOpen;
        }

        var merchant = await db.Merchants.SingleAsync(m => m.Id == merchantId, cancellationToken);
        var balance = await db.LedgerEntries
            .Where(line => line.MerchantId == merchantId && line.PayoutId == null)
            .SumAsync(line => line.Amount, cancellationToken);
        var asked = PayoutRequest.Ask(merchant, balance);
        if (asked.IsFailure)
        {
            return asked.Error!;
        }

        db.PayoutRequests.Add(asked.Value);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Asked twice at once: the open-request index let the first one in
            return AlreadyOpen;
        }

        return Result.Success();
    }

    /// <summary>The merchant takes back its open request.</summary>
    public async Task<Result> WithdrawAsync(CancellationToken cancellationToken = default)
    {
        var merchantId = currentUser.MerchantId ?? throw new InvalidOperationException("A merchant withdraws its request.");
        var request = await db.PayoutRequests.SingleOrDefaultAsync(
            r => r.MerchantId == merchantId && r.Status == PayoutRequestStatus.Open,
            cancellationToken);
        if (request is null)
        {
            return NotFound;
        }

        return await SaveAsync(request.Withdraw(time.GetUtcNow().UtcDateTime), cancellationToken);
    }

    /// <summary>Every open request, oldest first, with what the admin needs to decide on each.</summary>
    public async Task<IReadOnlyList<PayoutRequestReview>> OpenAsync(CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var today = tenant.Today(time.GetUtcNow().UtcDateTime);
        var open = await (
            from request in db.PayoutRequests
            join merchant in db.Merchants on request.MerchantId equals merchant.Id
            where request.Status == PayoutRequestStatus.Open
            orderby request.Id
            select new
            {
                request.Id,
                request.MerchantId,
                merchant.Name,
                request.Created,
                request.AmountAsked,
                merchant.PayoutMethod,
                merchant.PayoutAccount,
                merchant.PayoutHold
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        if (open.Count == 0)
        {
            return [];
        }

        var ids = open.Select(r => r.MerchantId).ToList();
        var balances = await db.LedgerEntries
            .Where(line => ids.Contains(line.MerchantId) && line.PayoutId == null)
            .GroupBy(line => line.MerchantId)
            .Select(g => new { MerchantId = g.Key, Balance = g.Sum(l => l.Amount), Today = g.Sum(l => l.EntryDate >= today ? l.Amount : 0) })
            .ToDictionaryAsync(b => b.MerchantId, cancellationToken);
        var withRiders = await (
            from attempt in db.DeliveryAttempts
            join run in db.DeliveryRuns on attempt.RunId equals run.Id
            where ids.Contains(attempt.MerchantId) && run.Status == RunStatus.Open &&
                (attempt.Outcome == AttemptOutcome.Delivered || attempt.Outcome == AttemptOutcome.PartlyDelivered) &&
                // Only cash in the balance to be paid: a parcel already paid out is the courier's matter with its rider
                db.LedgerEntries.Any(line => line.ParcelId == attempt.ParcelId && line.Kind == LedgerEntryKind.Cod && line.PayoutId == null)
            group attempt by attempt.MerchantId into cash
            select new { MerchantId = cash.Key, Amount = cash.Sum(a => a.CollectedAmount) })
            .ToDictionaryAsync(c => c.MerchantId, c => c.Amount, cancellationToken);
        var coming = ParcelStatuses.ToDeliver.Append(ParcelStatus.Returning).ToList();
        var onTheirWay = await db.Parcels
            .Where(p => ids.Contains(p.MerchantId) && coming.Contains(p.Status))
            .GroupBy(p => p.MerchantId)
            .Select(g => new { MerchantId = g.Key, Count = g.Count(), Charges = g.Sum(p => p.DeliveryCharge + p.ReturnCharge) })
            .ToDictionaryAsync(p => p.MerchantId, cancellationToken);

        return
        [
            .. open.Select(r => new PayoutRequestReview(
                r.Id,
                r.MerchantId,
                r.Name,
                tenant.Local(r.Created),
                r.AmountAsked,
                balances.GetValueOrDefault(r.MerchantId)?.Balance ?? 0,
                balances.GetValueOrDefault(r.MerchantId)?.Today ?? 0,
                withRiders.GetValueOrDefault(r.MerchantId),
                onTheirWay.GetValueOrDefault(r.MerchantId)?.Count ?? 0,
                onTheirWay.GetValueOrDefault(r.MerchantId)?.Charges ?? 0,
                r.PayoutMethod is { } method
                    ? $"{method.DisplayName()} " +
                      (PhoneNumber.Parse(r.PayoutAccount) is { IsSuccess: true } phone ? phone.Value.Local : r.PayoutAccount)
                    : null,
                r.PayoutHold))
        ];
    }

    /// <summary>
    /// The admin pays the request: the merchant's balance as it is now, today's lines too, is made into a payout and sent
    /// at once, by the same rules as the daily run. Nothing is paid while the balance is ৳0 or less, there is no payout
    /// account, or the courier holds the account's payouts; the request then stays open for the admin to refuse.
    /// </summary>
    public async Task<Result<PaidNow>> PayAsync(long id, CancellationToken cancellationToken = default)
    {
        var request = await db.PayoutRequests.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (request is null)
        {
            return NotFound;
        }

        if (!request.IsOpen)
        {
            return Error.Conflict("payoutRequest.answered", "This request was answered already.");
        }

        var paid = await payouts.PayNowAsync(request.MerchantId, cancellationToken);
        if (paid.IsFailure)
        {
            return paid.Error!;
        }

        var payout = await db.Payouts.SingleAsync(p => p.Number == paid.Value.Number, cancellationToken);
        request.Pay(payout, time.GetUtcNow().UtcDateTime);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The merchant withdrew it at that moment; the payout was made all the same, and it is what counts
            db.Entry(request).State = EntityState.Detached;
        }

        return paid.Value;
    }

    /// <summary>The admin says no, and why; the balance goes out with the daily payouts as usual.</summary>
    public async Task<Result> RefuseAsync(long id, string? answer, CancellationToken cancellationToken = default)
    {
        var request = await db.PayoutRequests.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (request is null)
        {
            return NotFound;
        }

        return await SaveAsync(request.Refuse(answer, time.GetUtcNow().UtcDateTime), cancellationToken);
    }

    private async Task<Result> SaveAsync(Result changed, CancellationToken cancellationToken)
    {
        if (changed.IsFailure)
        {
            return changed;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("payoutRequest.answered", "This request was answered at the same moment. Reload to see how.");
        }

        return Result.Success();
    }
}
