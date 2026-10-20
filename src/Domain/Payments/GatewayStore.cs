using Domain.Common;

namespace Domain.Payments;

/// <summary>
/// One of the courier's two SSLCommerz stores as the super admin typed it: the store ID, and the password already
/// encrypted, or null to keep the one saved.
/// </summary>
public sealed record StoreCredentials(string? StoreId, string? SealedPassword);

/// <summary>
/// The SSLCommerz stores the courier takes online payments with, kept by its super admin: a sandbox store (test
/// payments) and a live store (real money), either or both, and which one is in use. Only the one in use takes
/// payments; the other waits with its credentials, so switching back needs no typing. At most one row per courier
/// (<c>UX_GatewayStore_Tenant</c>); with none, the server's own <c>Sslcommerz:*</c> settings are used. Passwords are
/// as the app encrypted them: this entity never sees them in clear.
/// </summary>
public class GatewayStore : TenantEntity
{
    private GatewayStore()
    {
    }

    public string? SandboxStoreId { get; private set; }

    public string? SandboxPassword { get; private set; }

    public string? LiveStoreId { get; private set; }

    public string? LivePassword { get; private set; }

    /// <summary>The live store takes the payments (real money); otherwise the sandbox store (test payments).</summary>
    public bool IsLive { get; private set; }

    public bool HasSandbox => SandboxStoreId is not null;

    public bool HasLive => LiveStoreId is not null;

    /// <summary>The store in use: its ID and its encrypted password.</summary>
    public (string StoreId, string Password) InUse => IsLive ? (LiveStoreId!, LivePassword!) : (SandboxStoreId!, SandboxPassword!);

    public static Result<GatewayStore> Create(StoreCredentials sandbox, StoreCredentials live, bool isLive)
    {
        var store = new GatewayStore();
        var changed = store.Change(sandbox, live, isLive);

        return changed.IsSuccess ? store : changed.Error!;
    }

    /// <summary>
    /// Changes both stores and which is in use. A store with neither an ID nor a new password is taken off (unless it is
    /// the one in use); an ID with no new password keeps the password saved for that store.
    /// </summary>
    public Result Change(StoreCredentials sandbox, StoreCredentials live, bool isLive)
    {
        var newSandbox = Read(sandbox, SandboxPassword, "sandbox");
        if (newSandbox.IsFailure)
        {
            return newSandbox.Error!;
        }

        var newLive = Read(live, LivePassword, "live");
        if (newLive.IsFailure)
        {
            return newLive.Error!;
        }

        if ((isLive ? newLive.Value : newSandbox.Value) is null)
        {
            return Error.Validation(
                isLive ? "store.live" : "store.sandbox",
                isLive
                    ? "Enter the live store's ID and password before using it for payments."
                    : "Enter the sandbox store's ID and password before using it for payments.");
        }

        (SandboxStoreId, SandboxPassword) = newSandbox.Value ?? default;
        (LiveStoreId, LivePassword) = newLive.Value ?? default;
        IsLive = isLive;

        return Result.Success();
    }

    /// <summary>One store as typed: null when it is left empty, an error when it is half filled in.</summary>
    private static Result<(string? StoreId, string? Password)?> Read(StoreCredentials typed, string? savedPassword, string which)
    {
        var id = typed.StoreId.NullIfBlank();
        if (id is null)
        {
            return typed.SealedPassword is null
                ? Result.Success<(string?, string?)?>(null)
                : Error.Validation($"store.{which}.id", $"Enter the {which} store's ID as well as its password.");
        }

        if (id.Length > 100 || !id.All(char.IsAsciiLetterOrDigit))
        {
            return Error.Validation($"store.{which}.id", $"The {which} store ID is the one SSLCommerz gave you: letters and digits, such as onedr6ac6fd8338cc6.");
        }

        var password = typed.SealedPassword ?? savedPassword;
        if (password is null)
        {
            return Error.Validation($"store.{which}.password", $"Enter the {which} store's password SSLCommerz gave you.");
        }

        return Result.Success<(string?, string?)?>((id, password));
    }
}
