using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Domain.Merchants;

namespace Application.Merchants.Moderators;

/// <summary>
/// What the signed-in merchant login may do, and whether it is the account's owner or a moderator. After the courier
/// has terminated the account (<paramref name="Terminated"/>) everyone in it, owner and moderators alike, opens the same
/// pages as before but only to read them: the pages draw their forms disabled and the server refuses every post.
/// </summary>
public sealed record MerchantRights(bool IsOwner, bool IsStopped, MerchantPermissions Permissions, string? Name, bool Terminated = false)
{
    /// <summary>The owner of the account: everything, including the moderators themselves.</summary>
    public static readonly MerchantRights Owner = new(true, false, MerchantPermissions.All, null);

    public bool May(MerchantPermissions permission)
    {
        return !IsStopped && (permission == MerchantPermissions.None || Permissions.HasFlag(permission));
    }

    /// <summary>Only the owner adds moderators, changes what they may do and stops them.</summary>
    public bool MayManageModerators => IsOwner;
}

/// <summary>
/// Reads the signed-in login's rights in the merchant account. A login with no moderator row is the account's owner
/// and may do everything; a moderator may do what the owner ticked, and nothing at all once stopped. Scoped, so the
/// row is read once per request however many times a page asks.
/// </summary>
public class MerchantAccess(IAppDbContext db, ICurrentUser currentUser)
{
    private MerchantRights? rights;

    public async Task<MerchantRights> CurrentAsync(CancellationToken cancellationToken = default)
    {
        if (rights is not null)
        {
            return rights;
        }

        if (currentUser.UserId is not { } userId)
        {
            return rights = MerchantRights.Owner;
        }

        var moderator = await db.Moderators
            .AsNoTracking()
            .SingleOrDefaultAsync(m => m.UserId == userId, cancellationToken);

        // Termination is the account's: every business of it is archived together
        var terminated = currentUser.MerchantId is { } merchantId &&
            await db.Merchants.AnyAsync(m => m.Id == merchantId && m.Archived, cancellationToken);

        return rights = moderator is null
            ? MerchantRights.Owner with { Terminated = terminated }
            : new MerchantRights(false, moderator.Archived, moderator.Permissions, moderator.Name, terminated);
    }
}
