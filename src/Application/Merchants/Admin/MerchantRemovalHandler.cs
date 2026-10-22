using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Domain.Common;
using Domain.Merchants;
using Domain.Parcels;
using Domain.Payments;

namespace Application.Merchants.Admin;

/// <summary>
/// Takes a merchant's whole account (its main profile and every business) off the courier, for the super admin only.
/// <b>Terminate</b> ends an account that has worked here: it keeps its parcels, ledger and payouts on record, and its
/// people still sign in, but only to read them (<see cref="Moderators.MerchantRights.Terminated"/>): nobody books,
/// edits or changes anything, and its API keys are shut. It leaves the courier's lists. It waits until nothing is
/// still owed either way and no parcel is on the road. <b>Delete</b> erases an account that never did anything (a spam
/// sign-up, a merchant added by mistake) and is refused once it has any history.
/// </summary>
public class MerchantRemovalHandler(IAppDbContext db, IUserAccounts accounts, TimeProvider time)
{
    public async Task<Result> TerminateAsync(long id, CancellationToken cancellationToken = default)
    {
        var businesses = await AccountAsync(id, cancellationToken);
        if (businesses.Count == 0)
        {
            return AdminMerchantsHandler.NotFound;
        }

        var main = businesses.Single(m => m.IsMainProfile);
        if (main.Archived)
        {
            return Error.Conflict("merchant.terminated", $"{main.Name} has been terminated already.");
        }

        var ids = businesses.Select(m => m.Id).ToList();
        if (await StillOpenAsync(ids, cancellationToken) is { } open)
        {
            return Error.Conflict(
                "merchant.terminate",
                $"{main.Name} cannot be terminated yet: {open}.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Parcels not yet picked up are the only ones the courier does not have: they are called off, at no charge
        foreach (var parcel in await db.Parcels
            .Where(p => ids.Contains(p.MerchantId) && p.Status == ParcelStatus.Pending)
            .ToListAsync(cancellationToken))
        {
            parcel.Cancel("the courier closed the account", now);
        }

        businesses.ForEach(m => m.Terminate());
        foreach (var key in await db.MerchantApiKeys
            .Where(k => ids.Contains(k.MerchantId) && k.RevokedOn == null)
            .ToListAsync(cancellationToken))
        {
            key.Revoke(now);
        }

        // Logins and moderators are left alone: everyone in the account still signs in to read its history, and
        // MerchantRights holds them to that while the account is terminated
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Result.Success();
    }

    /// <summary>
    /// Brings a terminated account back: every business is active and listed again and its people can act again. What
    /// termination shut stays shut: its API keys (the merchant makes new ones) and the parcels that were called off.
    /// </summary>
    public async Task<Result> RestoreAsync(long id, CancellationToken cancellationToken = default)
    {
        var businesses = await AccountAsync(id, cancellationToken);
        if (businesses.Count == 0)
        {
            return AdminMerchantsHandler.NotFound;
        }

        var main = businesses.Single(m => m.IsMainProfile);
        if (!main.Archived)
        {
            return Error.Conflict("merchant.restore", $"{main.Name} has not been terminated.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var business in businesses)
        {
            var restored = business.Restore();
            if (restored.IsFailure && business.Archived)
            {
                return restored;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Result.Success();
    }

    public async Task<Result> DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        var businesses = await AccountAsync(id, cancellationToken);
        if (businesses.Count == 0)
        {
            return AdminMerchantsHandler.NotFound;
        }

        var main = businesses.Single(m => m.IsMainProfile);
        var ids = businesses.Select(m => m.Id).ToList();
        var history = await HistoryAsync(ids, cancellationToken);
        if (history is not null)
        {
            return Error.Conflict(
                "merchant.delete",
                $"{main.Name} cannot be deleted: it has {history}, which must stay on record. Terminate it instead.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var logins = await accounts.MerchantLoginsAsync(main.Id, cancellationToken);

        await db.MerchantApiKeys.Where(k => ids.Contains(k.MerchantId)).ExecuteDeleteAsync(cancellationToken);
        await db.PickupPoints.Where(p => ids.Contains(p.MerchantId)).ExecuteDeleteAsync(cancellationToken);
        await db.MerchantPictures.Where(p => ids.Contains(p.MerchantId)).ExecuteDeleteAsync(cancellationToken);
        await db.MerchantPayoutAccounts.Where(a => a.AccountId == main.Id).ExecuteDeleteAsync(cancellationToken);
        await db.Moderators.Where(m => m.AccountId == main.Id).ExecuteDeleteAsync(cancellationToken);

        // A merchant is last changed by its own owner, whose login cannot go while that row points at it
        await db.Merchants.Where(m => ids.Contains(m.Id)).ExecuteUpdateAsync(s => s.SetProperty(m => m.UpdatedId, (long?)null), cancellationToken);
        foreach (var login in logins)
        {
            var deleted = await accounts.DeleteLoginAsync(login, cancellationToken);
            if (deleted.IsFailure)
            {
                return deleted;
            }
        }

        await db.Merchants.Where(m => ids.Contains(m.Id) && m.MainMerchantId != null).ExecuteDeleteAsync(cancellationToken);
        await db.Merchants.Where(m => m.Id == main.Id).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Result.Success();
    }

    /// <summary>The main profile and every business of the account that business <paramref name="id"/> belongs to.</summary>
    private async Task<List<Merchant>> AccountAsync(long id, CancellationToken cancellationToken)
    {
        var account = await db.Merchants
            .Where(m => m.Id == id)
            .Select(m => m.MainMerchantId ?? m.Id)
            .SingleOrDefaultAsync(cancellationToken);

        return await db.Merchants
            .Where(m => m.Id == account || m.MainMerchantId == account)
            .ToListAsync(cancellationToken);
    }

    /// <summary>What still has to be settled before an account can end, or null when nothing does.</summary>
    private async Task<string?> StillOpenAsync(List<long> ids, CancellationToken cancellationToken)
    {
        List<string> open = [];
        var onTheRoad = await db.Parcels.CountAsync(
            p => ids.Contains(p.MerchantId) &&
                p.Status != ParcelStatus.Pending &&
                p.Status != ParcelStatus.Delivered &&
                p.Status != ParcelStatus.PartlyDelivered &&
                p.Status != ParcelStatus.Returned &&
                p.Status != ParcelStatus.Cancelled,
            cancellationToken);
        if (onTheRoad > 0)
        {
            open.Add($"{onTheRoad} parcel{(onTheRoad == 1 ? " is" : "s are")} still on the way (let them finish or be returned)");
        }

        var balance = await db.LedgerEntries
            .Where(e => ids.Contains(e.MerchantId) && e.PayoutId == null)
            .SumAsync(e => (decimal?)e.Amount, cancellationToken) ?? 0;
        if (balance > 0)
        {
            open.Add($"the courier still owes it ৳{balance:0} (pay it with Pay now first)");
        }
        else if (balance < 0)
        {
            open.Add($"it still owes the courier ৳{-balance:0} (settle it, or write it off with an adjustment, first)");
        }

        if (await db.Payouts.AnyAsync(p => ids.Contains(p.MerchantId) && p.Status == PayoutStatus.Pending, cancellationToken))
        {
            open.Add("a payout has not gone through yet (send it again or cancel it)");
        }

        if (await db.PayoutRequests.AnyAsync(r => ids.Contains(r.MerchantId) && r.Status == PayoutRequestStatus.Open, cancellationToken) ||
            await db.ParcelRequests.AnyAsync(r => ids.Contains(r.MerchantId) && r.Status == ParcelRequestStatus.Open, cancellationToken))
        {
            open.Add("a request is waiting for an answer");
        }

        if (await db.OnlinePayments.AnyAsync(
                p => ids.Contains(p.MerchantId) &&
                    (p.Status == OnlinePaymentStatus.Started || p.Status == OnlinePaymentStatus.Review || p.Status == OnlinePaymentStatus.RefundDue),
                cancellationToken))
        {
            open.Add("an online payment is still being settled");
        }

        return open.Count == 0 ? null : string.Join("; ", open);
    }

    /// <summary>What the account has done that must stay on record, or null when it never did anything.</summary>
    private async Task<string?> HistoryAsync(List<long> ids, CancellationToken cancellationToken)
    {
        List<string> found = [];
        if (await db.Parcels.AnyAsync(p => ids.Contains(p.MerchantId), cancellationToken))
        {
            found.Add("parcels");
        }

        if (await db.LedgerEntries.AnyAsync(e => ids.Contains(e.MerchantId), cancellationToken))
        {
            found.Add("money on its ledger");
        }

        if (await db.Payouts.AnyAsync(p => ids.Contains(p.MerchantId), cancellationToken) ||
            await db.PayoutRequests.AnyAsync(r => ids.Contains(r.MerchantId), cancellationToken) ||
            await db.OnlinePayments.AnyAsync(p => ids.Contains(p.MerchantId), cancellationToken))
        {
            found.Add("payments");
        }

        if (await db.PickupRequests.AnyAsync(r => ids.Contains(r.MerchantId), cancellationToken) ||
            await db.ReturnLists.AnyAsync(l => ids.Contains(l.MerchantId), cancellationToken))
        {
            found.Add("pickups or returns");
        }

        return found.Count == 0 ? null : string.Join(", ", found);
    }
}
