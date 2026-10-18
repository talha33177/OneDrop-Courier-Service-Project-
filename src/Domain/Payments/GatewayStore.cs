using Domain.Common;

namespace Domain.Payments;

/// <summary>
/// The SSLCommerz store the courier takes online payments with, kept by its super admin. At most one per courier
/// (<c>UX_GatewayStore_Tenant</c>); with none, the server's own <c>Sslcommerz:*</c> settings are used.
/// <see cref="StorePassword"/> is the password as the app encrypted it: this entity never sees it in clear.
/// </summary>
public class GatewayStore : TenantEntity
{
    private GatewayStore()
    {
    }

    public string StoreId { get; private set; } = "";

    public string StorePassword { get; private set; } = "";

    /// <summary>SSLCommerz's live gateway, which takes real money; otherwise its sandbox, which takes test payments.</summary>
    public bool IsLive { get; private set; }

    public static Result<GatewayStore> Create(string? storeId, bool isLive, string sealedPassword)
    {
        var store = new GatewayStore();
        var changed = store.Change(storeId, isLive, sealedPassword);

        return changed.IsSuccess ? store : changed.Error!;
    }

    /// <summary>Changes the store; a null <paramref name="sealedPassword"/> keeps the password it has.</summary>
    public Result Change(string? storeId, bool isLive, string? sealedPassword)
    {
        var id = storeId.NullIfBlank();
        if (id is null || id.Length > 100 || !id.All(char.IsAsciiLetterOrDigit))
        {
            return Error.Validation("store.id", "Enter the store ID SSLCommerz gave you: letters and digits, such as onedr6ac6fd8338cc6.");
        }

        if (sealedPassword is null && StorePassword.Length == 0)
        {
            return Error.Validation("store.password", "Enter the store password SSLCommerz gave you.");
        }

        StoreId = id;
        IsLive = isLive;
        StorePassword = sealedPassword ?? StorePassword;

        return Result.Success();
    }
}
