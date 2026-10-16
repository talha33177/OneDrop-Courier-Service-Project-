namespace Application.Abstractions;

/// <summary>
/// Who is calling. A merchant's API key has a <see cref="MerchantId"/> but no <see cref="UserId"/>;
/// background jobs have neither.
/// </summary>
public interface ICurrentUser
{
    long? UserId { get; }

    /// <summary>
    /// The business being worked in: for a merchant panel user, the one chosen among their account's businesses (the
    /// main profile until they choose); for an API key, its own. Switches on the merchant filter.
    /// </summary>
    long? MerchantId { get; }

    /// <summary>A merchant login's account: the main profile its businesses belong to. Null for an API key.</summary>
    long? AccountId { get; }

    /// <summary>Whether the signed-in login holds <paramref name="role"/> (see <c>Application.Common.Roles</c>).</summary>
    bool IsInRole(string role);
}
