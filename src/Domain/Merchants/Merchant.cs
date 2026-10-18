using Domain.Common;

namespace Domain.Merchants;

/// <summary>Stored as TINYINT. Never renumber a value that has been saved.</summary>
public enum MerchantStatus : byte
{
    /// <summary>Signed up and waiting for the courier to approve the account; cannot book parcels yet.</summary>
    Pending = 1,

    Active = 2,

    /// <summary>Stopped by the courier: cannot book parcels, sees its parcels and payments.</summary>
    Suspended = 3
}

/// <summary>Where a merchant's payouts are sent. Stored as TINYINT. Never renumber a value that has been saved.</summary>
public enum PayoutMethod : byte
{
    Bkash = 1,
    Nagad = 2,
    Bank = 3
}

/// <summary>The business details a merchant gives when signing up or editing its profile.</summary>
public sealed record MerchantProfile(
    string? Name,
    string? OwnerName,
    string? Phone,
    string? Email,
    string? Address);

/// <summary>
/// A shop that sends parcels with the courier. It books parcels on the panel or by API, has them collected from its
/// pickup points, pays the delivery charges out of the cash collected for it and is paid the rest
/// (<see cref="Payments.Payout"/>).
/// </summary>
public class Merchant : TenantEntity, IArchivable
{
    public const int MaxWebhookUrlLength = 500;

    public const int MaxPayoutHoldLength = 200;

    private Merchant()
    {
    }

    public string Name { get; private set; } = "";

    public string OwnerName { get; private set; } = "";

    /// <summary>E.164.</summary>
    public string ContactPhone { get; private set; } = "";

    public string? ContactEmail { get; private set; }

    /// <summary>The business address, for the invoice and the courier's records.</summary>
    public string Address { get; private set; } = "";

    public MerchantStatus Status { get; private set; }

    public PayoutMethod? PayoutMethod { get; private set; }

    /// <summary>The bKash or Nagad number (E.164), or the bank account number.</summary>
    public string? PayoutAccount { get; private set; }

    /// <summary>The name on the account; for a bank, also the bank and branch.</summary>
    public string? PayoutAccountName { get; private set; }

    /// <summary>Where the shop's parcel status changes are posted. Null: the shop takes no webhooks.</summary>
    public string? WebhookUrl { get; private set; }

    /// <summary>
    /// The key the shop checks our webhook signatures with (<see cref="WebhookSignature"/>). Stored as it is: signing
    /// needs the secret itself, unlike an API key, which is only compared. Kept when the URL is removed.
    /// </summary>
    public string? WebhookSecret { get; private set; }

    /// <summary>
    /// Why the courier holds the account's payouts; null while they go out as usual. Held lines keep counting and are
    /// paid once the hold is lifted.
    /// </summary>
    public string? PayoutHold { get; private set; }

    public bool Archived { get; private set; }

    /// <summary>
    /// Set on a business added under another merchant's account: the account's main profile. Its logins work in every
    /// business of the account; approval and the payout account are the account's, kept in step (<see cref="FollowAccount"/>).
    /// Null on a main profile.
    /// </summary>
    public long? MainMerchantId { get; private set; }

    public bool IsMainProfile => MainMerchantId is null;

    /// <summary>The account this merchant belongs to: its main profile.</summary>
    public long AccountId => MainMerchantId ?? Id;

    /// <summary>Only an active merchant books parcels and asks for pickups.</summary>
    public bool CanBook => Status == MerchantStatus.Active && !Archived;

    public bool HasPayoutAccount => PayoutMethod is not null && PayoutAccount is not null;

    public bool ArePayoutsHeld => PayoutHold is not null;

    /// <summary>A shop that signed up itself: it waits for the courier to approve it.</summary>
    public static Result<Merchant> SignUp(MerchantProfile profile)
    {
        var merchant = New(profile, MerchantStatus.Pending);
        if (merchant.IsSuccess)
        {
            merchant.Value.Raise(new MerchantAccountChanged(merchant.Value, MerchantNotice.SignedUp));
        }

        return merchant;
    }

    /// <summary>A shop the courier's admin added: it can book at once.</summary>
    public static Result<Merchant> Add(MerchantProfile profile)
    {
        return New(profile, MerchantStatus.Active);
    }

    /// <summary>
    /// Another business under <paramref name="main"/>'s account, with its own parcels, pickups, payments and balance.
    /// It starts with the account's approval and payout account, and the account's owner. <paramref name="businesses"/> is
    /// how many the account has added already; <paramref name="limit"/> the courier's most (null for no limit).
    /// </summary>
    public static Result<Merchant> AddBusiness(Merchant main, MerchantProfile profile, int businesses, int? limit)
    {
        if (!main.IsMainProfile)
        {
            return Error.Validation("merchant.business.main", "Add a business from the account's main profile.");
        }

        if (limit is { } most && businesses >= most)
        {
            return Error.Conflict(
                "merchant.business.limit",
                $"An account can have {most} businesses besides its main profile, and yours has them all.");
        }

        if (main.Archived || main.Status == MerchantStatus.Suspended)
        {
            return Error.Conflict(
                "merchant.business.suspended",
                "Your account is suspended, so no business can be added to it. Call the hotline to sort it out.");
        }

        var business = New(profile with { OwnerName = main.OwnerName }, main.Status);
        if (business.IsFailure)
        {
            return business;
        }

        business.Value.MainMerchantId = main.Id;
        business.Value.FollowAccount(main);

        return business;
    }

    /// <summary>
    /// Takes the account's decisions from its main profile: whether the courier approved or suspended it, and where its
    /// payouts go. Called for every business of an account when its main profile changes either.
    /// </summary>
    public void FollowAccount(Merchant main)
    {
        if (main.Id != AccountId)
        {
            throw new InvalidOperationException($"{Name} is not a business of {main.Name}'s account.");
        }

        Status = main.Status;
        PayoutMethod = main.PayoutMethod;
        PayoutAccount = main.PayoutAccount;
        PayoutAccountName = main.PayoutAccountName;
        PayoutHold = main.PayoutHold;
    }

    public Result Edit(MerchantProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Trim().Length > 200)
        {
            return Error.Validation("merchant.name", "Enter the business name, at most 200 characters.");
        }

        if (string.IsNullOrWhiteSpace(profile.OwnerName) || profile.OwnerName.Trim().Length > 200)
        {
            return Error.Validation("merchant.owner", "Enter the owner's name, at most 200 characters.");
        }

        var phone = PhoneNumber.Parse(profile.Phone);
        if (phone.IsFailure)
        {
            return phone.Error!;
        }

        var email = profile.Email.NullIfBlank();
        if (email is not null && (email.Length > 320 || !email.Contains('@')))
        {
            return Error.Validation("merchant.email", "Enter a valid email address.");
        }

        if (string.IsNullOrWhiteSpace(profile.Address) || profile.Address.Trim().Length > 500)
        {
            return Error.Validation("merchant.address", "Enter the business address, at most 500 characters.");
        }

        Name = profile.Name.Trim();
        OwnerName = profile.OwnerName.Trim();
        ContactPhone = phone.Value.Value;
        ContactEmail = email;
        Address = profile.Address.Trim();

        return Result.Success();
    }

    public Result Approve()
    {
        if (Status != MerchantStatus.Pending)
        {
            return Error.Conflict("merchant.approve", $"{Name} is not waiting for approval.");
        }

        Status = MerchantStatus.Active;
        Raise(new MerchantAccountChanged(this, MerchantNotice.Approved));

        return Result.Success();
    }

    public Result Suspend()
    {
        if (Status == MerchantStatus.Suspended)
        {
            return Error.Conflict("merchant.suspend", $"{Name} is already suspended.");
        }

        Status = MerchantStatus.Suspended;
        Raise(new MerchantAccountChanged(this, MerchantNotice.Suspended));

        return Result.Success();
    }

    public Result Reactivate()
    {
        if (Status != MerchantStatus.Suspended)
        {
            return Error.Conflict("merchant.reactivate", $"{Name} is not suspended.");
        }

        Status = MerchantStatus.Active;
        Raise(new MerchantAccountChanged(this, MerchantNotice.Reactivated));

        return Result.Success();
    }

    /// <summary>The courier stops paying the account out, with why (the merchant reads it on its Payments page).</summary>
    public Result HoldPayouts(string? reason)
    {
        var why = reason?.Trim();
        if (string.IsNullOrEmpty(why) || why.Length > MaxPayoutHoldLength)
        {
            return Error.Validation("merchant.hold", $"Say why the payouts are held, in at most {MaxPayoutHoldLength} characters.");
        }

        PayoutHold = why;
        Raise(new MerchantAccountChanged(this, MerchantNotice.PayoutsHeld));

        return Result.Success();
    }

    public Result ReleasePayouts()
    {
        if (PayoutHold is null)
        {
            return Error.Conflict("merchant.release", $"{Name}'s payouts are not held.");
        }

        PayoutHold = null;
        Raise(new MerchantAccountChanged(this, MerchantNotice.PayoutsReleased));

        return Result.Success();
    }

    /// <summary>
    /// Where payouts go: a bKash or Nagad mobile number, or a bank account with the bank and branch in
    /// <paramref name="accountName"/>.
    /// </summary>
    public Result SetPayoutAccount(PayoutMethod method, string? account, string? accountName)
    {
        var read = PayoutAccounts.Read(method, account, accountName);
        if (read.IsFailure)
        {
            return read.Error!;
        }

        PayoutMethod = method;
        PayoutAccount = read.Value.Number;
        PayoutAccountName = read.Value.Name;

        return Result.Success();
    }

    /// <summary>
    /// Sets where webhooks go, and gives the shop a secret the first time. The address must be https, so the parcel
    /// data and the signature cross the internet encrypted; plain http only to this machine (localhost), for testing.
    /// </summary>
    public Result SetWebhook(string? url)
    {
        var trimmed = url.NullIfBlank();
        if (trimmed is null || trimmed.Length > MaxWebhookUrlLength ||
            !Uri.TryCreate(trimmed, UriKind.Absolute, out var address) ||
            address.Scheme is not ("https" or "http") || address.UserInfo.Length > 0)
        {
            return Error.Validation(
                "merchant.webhook.url",
                $"Enter the full address of your webhook, starting with https://, at most {MaxWebhookUrlLength} " +
                "characters.");
        }

        if (address.Scheme == "http" && !address.IsLoopback &&
            !address.Host.EndsWith(".localhost", StringComparison.Ordinal))
        {
            return Error.Validation("merchant.webhook.https", "The webhook address must start with https://.");
        }

        WebhookUrl = address.AbsoluteUri;
        WebhookSecret ??= WebhookSignature.NewSecret();

        return Result.Success();
    }

    /// <summary>A new secret, for when the old one may have leaked. Signatures made with the old one stop verifying.</summary>
    public void NewWebhookSecret()
    {
        WebhookSecret = WebhookSignature.NewSecret();
    }

    public void RemoveWebhook()
    {
        WebhookUrl = null;
    }

    private static Result<Merchant> New(MerchantProfile profile, MerchantStatus status)
    {
        var merchant = new Merchant { Status = status };
        var edited = merchant.Edit(profile);

        return edited.IsSuccess ? merchant : edited.Error!;
    }
}

/// <summary>What happened to a merchant account that its owner (and, for a sign-up, the courier) is emailed about.</summary>
public enum MerchantNotice
{
    SignedUp = 1,
    Approved = 2,
    Suspended = 3,
    Reactivated = 4,
    PayoutsHeld = 5,
    PayoutsReleased = 6
}

/// <summary>A merchant account signed up, was approved, suspended or let back in, or had its payouts held or released.</summary>
public sealed record MerchantAccountChanged(Merchant Merchant, MerchantNotice Notice) : IDomainEvent;
