using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Application.Merchants.Onboarding;
using Domain.Common;
using Domain.Delivery;
using Domain.Merchants;
using Domain.Parcels;
using Domain.Payments;

namespace Application.Merchants.Admin;

public sealed record MerchantRow(
    long Id,
    string Name,
    string OwnerName,
    string Phone,
    MerchantStatus Status,
    int Parcels,
    int Delivered,
    int Returned,
    decimal Unpaid,
    DateTime Joined)
{
    /// <summary>For a business added under another merchant's account: the name of its main profile.</summary>
    public string? Account { get; init; }
}

/// <param name="Pin">The door on the map, when the merchant pinned it: the rider navigates to it.</param>
public sealed record PickupPointView(
    long Id,
    string Name,
    string Address,
    string Area,
    long AreaId,
    string ContactPhone,
    bool IsDefault)
{
    public GeoPoint? Pin { get; init; }
}

public sealed record MerchantView(
    long Id,
    MerchantProfile Profile,
    MerchantStatus Status,
    PayoutMethod? PayoutMethod,
    string? PayoutAccount,
    string? PayoutAccountName,
    string? LoginEmail,
    IReadOnlyList<PickupPointView> PickupPoints,
    int Parcels,
    int Delivered,
    int Returned,
    decimal Unpaid,
    DateTime Joined)
{
    /// <summary>For a business added under another merchant's account: that account's main profile.</summary>
    public AccountLink? Account { get; init; }

    /// <summary>Why the courier holds the account's payouts; null while they go out.</summary>
    public string? PayoutHold { get; init; }

    /// <summary>The courier ended this business for good: its history stays, nothing else works.</summary>
    public bool Terminated { get; init; }

    /// <summary>The adjustments the courier wrote on this business, newest first.</summary>
    public IReadOnlyList<AdjustmentRow> Adjustments { get; init; } = [];

    public int Cancelled { get; init; }

    /// <summary>What paying now would send that the daily run would not yet: lines dated today.</summary>
    public decimal UnpaidToday { get; init; }

    /// <summary>Cash in the unpaid balance that riders collected and have not yet handed in (their run is open).</summary>
    public decimal CashWithRiders { get; init; }

    /// <summary>Parcels still on their way, and the delivery and return charges the merchant owes if they come back.</summary>
    public int OnTheWay { get; init; }

    public decimal ChargesIfReturned { get; init; }

    /// <summary>Parcels booked and not yet finished: with the courier, or waiting for their pickup.</summary>
    public int InProgress => Parcels - Delivered - Returned - Cancelled;

    /// <summary>Returned out of the parcels that reached an end with the courier, as on the merchant's stats page.</summary>
    public double? ReturnRate => Delivered + Returned == 0 ? null : (double)Returned / (Delivered + Returned);
}

/// <summary>
/// Everything around one business the admin may need while on the phone with its merchant: the account it is part of,
/// the people who work in it, where its money goes, what it was paid and what it asked for.
/// </summary>
public sealed record MerchantAccountView(
    IReadOnlyList<BusinessLine> Businesses,
    IReadOnlyList<ModeratorLine> Moderators,
    IReadOnlyList<PayoutAccountLine> PayoutAccounts,
    IReadOnlyList<PayoutLine> Payouts,
    IReadOnlyList<RequestLine> Requests);

/// <summary>One business of the account; <see cref="OnTheWay"/> counts its parcels not yet finished.</summary>
public sealed record BusinessLine(long Id, string Name, MerchantStatus Status, bool IsMain, int OnTheWay, decimal Unpaid);

public sealed record ModeratorLine(long Id, string Name, string Email, MerchantPermissions Permissions, bool Stopped);

public sealed record PayoutAccountLine(PayoutMethod Method, string Number, string Name, bool InUse);

public sealed record PayoutLine(string Number, DateOnly UpTo, decimal Amount, PayoutStatus Status, bool Stuck);

/// <summary>A request from the merchant: about a parcel (its tracking code) or to be paid now (no code).</summary>
public sealed record RequestLine(DateTime Asked, string? TrackingCode, string What, decimal? Amount, string Status, bool Open);

public sealed record AccountLink(long Id, string Name);

/// <summary>An adjustment on a merchant's balance; <see cref="Payout"/> is the invoice that paid it, once paid.</summary>
public sealed record AdjustmentRow(DateOnly Date, decimal Amount, string Note, string? Payout);

/// <summary>
/// The courier's merchants: list and search them, add one (active at once), approve a sign-up, suspend or reactivate,
/// correct the profile and the payout account. Courier admins only.
/// </summary>
public class AdminMerchantsHandler(
    IAppDbContext db,
    ITenantContext tenantContext,
    MerchantOnboarding onboarding,
    IUserAccounts accounts,
    TimeProvider time)
{
    public static readonly Error NotFound = Error.NotFound("merchant.notFound", "That merchant was not found.");

    /// <summary>How many merchants a page of the list shows.</summary>
    public const int PageSize = 50;

    /// <summary>The merchants of a status (or every one), sign-ups first, a page at a time.</summary>
    public async Task<Page<MerchantRow>> ListAsync(
        MerchantStatus? status,
        string? search,
        int page = 1,
        bool terminated = false,
        CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var merchants = db.Merchants.Where(m => m.Archived == terminated);
        if (status is { } only)
        {
            merchants = merchants.Where(m => m.Status == only);
        }

        var text = search?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            // The merchant ID a merchant reads out from its panel, its phone, or part of its name or owner's
            var phone = PhoneNumber.Parse(text);
            var value = phone.IsSuccess ? phone.Value.Value : text;
            long? id = long.TryParse(text.TrimStart('#'), out var number) ? number : null;
            merchants = merchants.Where(m => m.Id == id || m.Name.Contains(text) || m.OwnerName.Contains(text) || m.ContactPhone == value);
        }

        var total = await merchants.CountAsync(cancellationToken);
        var shown = Math.Clamp(page, 1, Math.Max(1, (total + PageSize - 1) / PageSize));
        var rows = await merchants
            .OrderBy(m => m.Status)
            .ThenBy(m => m.Name)
            .ThenBy(m => m.Id)
            .Skip((shown - 1) * PageSize)
            .Take(PageSize)
            .Select(m => new MerchantRow(
                m.Id,
                m.Name,
                m.OwnerName,
                m.ContactPhone,
                m.Status,
                db.Parcels.Count(p => p.MerchantId == m.Id),
                db.Parcels.Count(p => p.MerchantId == m.Id && (p.Status == ParcelStatus.Delivered || p.Status == ParcelStatus.PartlyDelivered)),
                db.Parcels.Count(p => p.MerchantId == m.Id && p.Status == ParcelStatus.Returned),
                db.LedgerEntries.Where(e => e.MerchantId == m.Id && e.PayoutId == null).Sum(e => (decimal?)e.Amount) ?? 0,
                m.Created)
            {
                Account = db.Merchants.Where(a => a.Id == m.MainMerchantId).Select(a => a.Name).FirstOrDefault()
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return new Page<MerchantRow>([.. rows.Select(r => r with { Joined = tenant.Local(r.Joined) })], total, shown, PageSize);
    }

    /// <summary>How many sign-ups wait for approval.</summary>
    public Task<int> WaitingAsync(CancellationToken cancellationToken = default)
    {
        return db.Merchants.CountAsync(m => m.Status == MerchantStatus.Pending && !m.Archived, cancellationToken);
    }

    public async Task<Result<MerchantView>> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var merchant = await db.Merchants.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (merchant is null)
        {
            return NotFound;
        }

        var points = await (
            from point in db.PickupPoints
            join area in db.Areas on point.AreaId equals area.Id
            where point.MerchantId == id && !point.Archived
            orderby point.IsDefault descending, point.Name
            select new PickupPointView(point.Id, point.Name, point.Address, area.Name, area.Id, point.ContactPhone, point.IsDefault))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var counts = await db.Parcels
            .Where(p => p.MerchantId == id)
            .GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Status, g => g.Count, cancellationToken);
        var unpaid = await db.LedgerEntries
            .Where(e => e.MerchantId == id && e.PayoutId == null)
            .SumAsync(e => (decimal?)e.Amount, cancellationToken) ?? 0;
        var today = tenant.Today(time.GetUtcNow().UtcDateTime);
        var unpaidToday = await db.LedgerEntries
            .Where(e => e.MerchantId == id && e.PayoutId == null && e.EntryDate >= today)
            .SumAsync(e => (decimal?)e.Amount, cancellationToken) ?? 0;
        var cashWithRiders = await (
            from attempt in db.DeliveryAttempts
            join run in db.DeliveryRuns on attempt.RunId equals run.Id
            where attempt.MerchantId == id && run.Status == RunStatus.Open &&
                (attempt.Outcome == AttemptOutcome.Delivered || attempt.Outcome == AttemptOutcome.PartlyDelivered) &&
                // Only cash still in the balance: a parcel already paid out is the courier's matter with its rider
                db.LedgerEntries.Any(line => line.ParcelId == attempt.ParcelId && line.Kind == LedgerEntryKind.Cod && line.PayoutId == null)
            select (decimal?)attempt.CollectedAmount)
            .SumAsync(cancellationToken) ?? 0;
        var coming = ParcelStatuses.ToDeliver.Append(ParcelStatus.Returning).ToList();
        var onTheWay = await db.Parcels
            .Where(p => p.MerchantId == id && coming.Contains(p.Status))
            .GroupBy(p => 1)
            .Select(g => new { Count = g.Count(), Charges = g.Sum(p => p.DeliveryCharge + p.ReturnCharge) })
            .SingleOrDefaultAsync(cancellationToken);

        return new MerchantView(
            merchant.Id,
            new MerchantProfile(merchant.Name, merchant.OwnerName, merchant.ContactPhone, merchant.ContactEmail, merchant.Address),
            merchant.Status,
            merchant.PayoutMethod,
            merchant.PayoutAccount,
            merchant.PayoutAccountName,
            await accounts.MerchantEmailAsync(merchant.AccountId, cancellationToken),
            points,
            counts.Values.Sum(),
            counts.GetValueOrDefault(ParcelStatus.Delivered) + counts.GetValueOrDefault(ParcelStatus.PartlyDelivered),
            counts.GetValueOrDefault(ParcelStatus.Returned),
            unpaid,
            tenant.Local(merchant.Created))
        {
            Account = merchant.MainMerchantId is { } main
                ? await db.Merchants.Where(a => a.Id == main).Select(a => new AccountLink(a.Id, a.Name)).SingleAsync(cancellationToken)
                : null,
            PayoutHold = merchant.PayoutHold,
            Terminated = merchant.Archived,
            Cancelled = counts.GetValueOrDefault(ParcelStatus.Cancelled),
            UnpaidToday = unpaidToday,
            CashWithRiders = cashWithRiders,
            OnTheWay = onTheWay?.Count ?? 0,
            ChargesIfReturned = onTheWay?.Charges ?? 0,
            Adjustments = await (
                from line in db.LedgerEntries
                join payout in db.Payouts on line.PayoutId equals (long?)payout.Id into paid
                from payout in paid.DefaultIfEmpty()
                where line.MerchantId == id && line.Kind == LedgerEntryKind.Adjustment
                orderby line.Id descending
                select new AdjustmentRow(line.EntryDate, line.Amount, line.Note!, payout == null ? null : payout.Number))
                .Take(20)
                .AsNoTracking()
                .ToListAsync(cancellationToken)
        };
    }

    /// <summary>
    /// The account around business <paramref name="id"/>: every business of it, its moderators and saved payout
    /// accounts (all the account's), and this business's latest payouts and requests.
    /// </summary>
    public async Task<Result<MerchantAccountView>> AccountAsync(long id, CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var merchant = await db.Merchants.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (merchant is null)
        {
            return NotFound;
        }

        var accountId = merchant.AccountId;
        var main = accountId == merchant.Id
            ? merchant
            : await db.Merchants.AsNoTracking().SingleAsync(m => m.Id == accountId, cancellationToken);
        var businesses = await db.Merchants
            .Where(m => m.Id == accountId || m.MainMerchantId == accountId)
            .OrderBy(m => m.MainMerchantId != null)
            .ThenBy(m => m.Name)
            .Select(m => new BusinessLine(
                m.Id,
                m.Name,
                m.Status,
                m.MainMerchantId == null,
                db.Parcels.Count(p => p.MerchantId == m.Id &&
                    p.Status != ParcelStatus.Delivered &&
                    p.Status != ParcelStatus.PartlyDelivered &&
                    p.Status != ParcelStatus.Returned &&
                    p.Status != ParcelStatus.Cancelled),
                db.LedgerEntries.Where(e => e.MerchantId == m.Id && e.PayoutId == null).Sum(e => (decimal?)e.Amount) ?? 0))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var moderators = await db.Moderators
            .Where(m => m.AccountId == accountId)
            .OrderBy(m => m.Archived)
            .ThenBy(m => m.Name)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var emails = await accounts.EmailsAsync([.. moderators.Select(m => m.UserId)], cancellationToken);

        var saved = await db.MerchantPayoutAccounts
            .Where(a => a.AccountId == accountId && !a.Archived)
            .OrderBy(a => a.Method)
            .ThenBy(a => a.Number)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var payouts = await db.Payouts
            .Where(p => p.MerchantId == id)
            .OrderByDescending(p => p.Id)
            .Take(10)
            .Select(p => new PayoutLine(p.Number, p.UpToDate, p.Amount, p.Status, p.Status == PayoutStatus.Pending && p.FailedAttempts > 0))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var parcelRequests = await (
            from request in db.ParcelRequests
            join parcel in db.Parcels on request.ParcelId equals parcel.Id
            where request.MerchantId == id
            orderby request.Id descending
            select new { request.Created, parcel.TrackingCode, request.Kind, request.NewCodAmount, request.Status })
            .Take(10)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var payoutRequests = await db.PayoutRequests
            .Where(r => r.MerchantId == id)
            .OrderByDescending(r => r.Id)
            .Take(5)
            .Select(r => new { r.Created, r.AmountAsked, r.Status })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        RequestLine[] requests =
        [
            .. parcelRequests.Select(r => new RequestLine(
                tenant.Local(r.Created),
                r.TrackingCode,
                r.Kind == ParcelRequestKind.Cancel ? "Cancel the parcel" : "Change the cash to",
                r.NewCodAmount,
                r.Status.ToString(),
                r.Status == ParcelRequestStatus.Open)),
            .. payoutRequests.Select(r => new RequestLine(
                tenant.Local(r.Created),
                null,
                "Be paid now",
                r.AmountAsked,
                r.Status.ToString(),
                r.Status == PayoutRequestStatus.Open))
        ];

        return new MerchantAccountView(
            businesses,
            [.. moderators.Select(m => new ModeratorLine(m.Id, m.Name, emails.GetValueOrDefault(m.UserId, ""), m.Permissions, m.Archived))],
            [.. saved.Select(a => new PayoutAccountLine(a.Method, a.Number, a.Name, a.IsInUse(main)))],
            payouts,
            [.. requests.OrderByDescending(r => r.Asked)]);
    }

    public Task<Result<long>> AddAsync(NewMerchant spec, CancellationToken cancellationToken = default)
    {
        return onboarding.CreateAsync(spec, approved: true, cancellationToken);
    }

    public Task<Result> EditAsync(long id, MerchantProfile profile, CancellationToken cancellationToken = default)
    {
        return ChangeAsync(id, merchant => merchant.Edit(profile), cancellationToken);
    }

    public Task<Result> ApproveAsync(long id, CancellationToken cancellationToken = default)
    {
        return ChangeAccountAsync(id, merchant => merchant.Approve(), cancellationToken);
    }

    public Task<Result> SuspendAsync(long id, CancellationToken cancellationToken = default)
    {
        return ChangeAccountAsync(id, merchant => merchant.Suspend(), cancellationToken);
    }

    public Task<Result> ReactivateAsync(long id, CancellationToken cancellationToken = default)
    {
        return ChangeAccountAsync(id, merchant => merchant.Reactivate(), cancellationToken);
    }

    /// <summary>Stops one of an account's moderators: their login is shut out at once, as when the owner stops them.</summary>
    public async Task<Result> StopModeratorAsync(long moderatorId, CancellationToken cancellationToken = default)
    {
        var moderator = await db.Moderators.SingleOrDefaultAsync(m => m.Id == moderatorId, cancellationToken);
        if (moderator is null)
        {
            return Error.NotFound("moderator.notFound", "That person was not found.");
        }

        moderator.Stop();
        await db.SaveChangesAsync(cancellationToken);
        await accounts.SetLoginEnabledAsync(moderator.UserId, enabled: false, cancellationToken);

        return Result.Success();
    }

    /// <summary>Lets a stopped moderator back in.</summary>
    public async Task<Result> LetModeratorBackInAsync(long moderatorId, CancellationToken cancellationToken = default)
    {
        var moderator = await db.Moderators.SingleOrDefaultAsync(m => m.Id == moderatorId, cancellationToken);
        if (moderator is null)
        {
            return Error.NotFound("moderator.notFound", "That person was not found.");
        }

        moderator.LetBackIn();
        await db.SaveChangesAsync(cancellationToken);
        await accounts.SetLoginEnabledAsync(moderator.UserId, enabled: true, cancellationToken);

        return Result.Success();
    }

    /// <summary>Stops the account's payouts, every business of it, with why; its lines keep counting.</summary>
    public Task<Result> HoldPayoutsAsync(long id, string? reason, CancellationToken cancellationToken = default)
    {
        return ChangeAccountAsync(id, merchant => merchant.HoldPayouts(reason), cancellationToken);
    }

    public Task<Result> ReleasePayoutsAsync(long id, CancellationToken cancellationToken = default)
    {
        return ChangeAccountAsync(id, merchant => merchant.ReleasePayouts(), cancellationToken);
    }

    /// <summary>
    /// Writes an adjustment on the merchant's balance, dated today: positive credits it (it paid what it owed, or is
    /// compensated), negative charges it. It goes out with the next payout like any other line.
    /// </summary>
    public async Task<Result> AdjustAsync(long id, decimal amount, string? note, CancellationToken cancellationToken = default)
    {
        if (!await db.Merchants.AnyAsync(m => m.Id == id, cancellationToken))
        {
            return NotFound;
        }

        var line = LedgerEntry.Adjust(id, amount, note, tenantContext.Require().Today(time.GetUtcNow().UtcDateTime));
        if (line.IsFailure)
        {
            return line.Error!;
        }

        line.Value.TellMerchant();
        db.LedgerEntries.Add(line.Value);
        await db.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    /// <summary>Sets where the account's payouts go, and keeps the account among its saved ones.</summary>
    public async Task<Result> SetPayoutAsync(long id, PayoutMethod method, string? account, string? name, CancellationToken cancellationToken = default)
    {
        var changed = await ChangeAccountAsync(id, merchant => merchant.SetPayoutAccount(method, account, name), cancellationToken);
        if (changed.IsFailure)
        {
            return changed;
        }

        var accountId = await db.Merchants.Where(m => m.Id == id).Select(m => m.MainMerchantId ?? m.Id).SingleAsync(cancellationToken);
        var kept = MerchantPayoutAccount.Create(accountId, method, account, name).Value;
        if (!await db.MerchantPayoutAccounts.AnyAsync(
                a => a.AccountId == accountId && !a.Archived && a.Method == kept.Method && a.Number == kept.Number,
                cancellationToken))
        {
            db.MerchantPayoutAccounts.Add(kept);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }

    /// <summary>
    /// Approval and the payout account belong to the merchant's account: the change is made on its main profile, from
    /// whichever business it was asked on, and every business of the account follows.
    /// </summary>
    private async Task<Result> ChangeAccountAsync(long id, Func<Merchant, Result> change, CancellationToken cancellationToken)
    {
        var account = await db.Merchants.Where(m => m.Id == id).Select(m => m.MainMerchantId ?? m.Id).SingleOrDefaultAsync(cancellationToken);

        return await ChangeAsync(account, change, cancellationToken, follow: true);
    }

    private async Task<Result> ChangeAsync(long id, Func<Merchant, Result> change, CancellationToken cancellationToken, bool follow = false)
    {
        var merchant = await db.Merchants.SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (merchant is null)
        {
            return NotFound;
        }

        var changed = change(merchant);
        if (changed.IsFailure)
        {
            return changed;
        }

        if (follow)
        {
            var businesses = await db.Merchants.Where(m => m.MainMerchantId == merchant.Id).ToListAsync(cancellationToken);
            businesses.ForEach(business => business.FollowAccount(merchant));
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Error.Conflict("merchant.name.taken", "A merchant with this business name already exists.");
        }

        return Result.Success();
    }
}
