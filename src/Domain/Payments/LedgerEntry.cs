using Domain.Common;
using Domain.Parcels;

namespace Domain.Payments;

/// <summary>What a ledger entry records. Stored as TINYINT. Never renumber a value that has been saved.</summary>
public enum LedgerEntryKind : byte
{
    /// <summary>Cash collected at the door for the merchant: owed to the merchant.</summary>
    Cod = 1,

    /// <summary>The parcel's delivery charge: the merchant pays it whether the parcel was delivered or returned.</summary>
    DeliveryCharge = 2,

    /// <summary>The per-cent charge on the cash collected.</summary>
    CodCharge = 3,

    /// <summary>Charged on top of the delivery charge for a parcel that came back.</summary>
    ReturnCharge = 4,

    /// <summary>
    /// Written by the courier, not by a parcel, with a reason: positive when it credits the merchant (the merchant paid
    /// the courier what it owed, or is compensated), negative when it charges the merchant.
    /// </summary>
    Adjustment = 5
}

/// <summary>
/// One line of what the courier owes a merchant for one of its parcels: <see cref="Amount"/> is positive when owed to
/// the merchant (its cash on delivery) and negative when the merchant owes it (a charge). <see cref="EntryDate"/> is the
/// tenant's day it belongs to. A line is paid out once, by the <see cref="Payout"/> that takes it; until then it counts
/// towards the merchant's next payout, so charges the day's cash does not cover are carried forward. One line of each
/// kind per parcel, and adjustments the courier writes by hand. A line of ৳0 is never written.
/// </summary>
public class LedgerEntry : TenantEntity, IMerchantOwned
{
    public const int MaxNoteLength = 200;

    public const decimal MaxAdjustment = 1_000_000m;

    private LedgerEntry()
    {
    }

    public long MerchantId { get; private set; }

    /// <summary>The parcel the line is for; null for an <see cref="LedgerEntryKind.Adjustment"/>.</summary>
    public long? ParcelId { get; private set; }

    public LedgerEntryKind Kind { get; private set; }

    public decimal Amount { get; private set; }

    public DateOnly EntryDate { get; private set; }

    /// <summary>Why the courier wrote an adjustment; null on a parcel's lines.</summary>
    public string? Note { get; private set; }

    /// <summary>The payout that paid the line; null while it waits for the next one.</summary>
    public long? PayoutId { get; private set; }

    public Payout? Payout { get; private set; }

    /// <summary>Two payouts taking the same line at once: the second one's save fails.</summary>
    public byte[] RowVersion { get; private set; } = [];

    public bool IsPaidOut => PayoutId is not null || Payout is not null;

    /// <summary>The lines of a parcel that has just reached a final status: delivered, partly delivered or returned.</summary>
    public static IReadOnlyList<LedgerEntry> For(Parcel parcel, DateOnly entryDate)
    {
        if (parcel.IsNew)
        {
            throw new InvalidOperationException("Save the parcel before writing its ledger lines.");
        }

        (LedgerEntryKind Kind, decimal Amount)[] lines = parcel.Status switch
        {
            ParcelStatus.Delivered or ParcelStatus.PartlyDelivered =>
            [
                (LedgerEntryKind.Cod, parcel.CollectedAmount!.Value),
                (LedgerEntryKind.DeliveryCharge, -parcel.DeliveryCharge),
                (LedgerEntryKind.CodCharge, -parcel.CodCharge!.Value)
            ],
            ParcelStatus.Returned =>
            [
                (LedgerEntryKind.DeliveryCharge, -parcel.DeliveryCharge),
                (LedgerEntryKind.ReturnCharge, -parcel.ReturnCharge)
            ],
            _ => throw new InvalidOperationException(
                $"Ledger lines are written for a delivered or returned parcel, not one that is {parcel.Status}.")
        };

        return
        [
            .. lines
                .Where(line => line.Amount != 0)
                .Select(line => new LedgerEntry
                {
                    MerchantId = parcel.MerchantId,
                    ParcelId = parcel.Id,
                    Kind = line.Kind,
                    Amount = line.Amount,
                    EntryDate = entryDate
                })
        ];
    }

    /// <summary>
    /// A line the courier writes on a merchant's balance by hand, with why: <paramref name="amount"/> positive credits
    /// the merchant (it paid what it owed, or is compensated), negative charges it. It counts towards the next payout
    /// like any other line.
    /// </summary>
    public static Result<LedgerEntry> Adjust(long merchantId, decimal amount, string? note, DateOnly entryDate)
    {
        if (amount == 0 || Math.Abs(amount) > MaxAdjustment || decimal.Round(amount, 2) != amount)
        {
            return Error.Validation("ledger.amount", $"Enter an amount other than ৳0, at most ৳{MaxAdjustment:N0}, in taka and paisa.");
        }

        var why = note?.Trim();
        if (string.IsNullOrEmpty(why) || why.Length > MaxNoteLength)
        {
            return Error.Validation("ledger.note", $"Say why, in at most {MaxNoteLength} characters: the merchant reads it on its statement.");
        }

        return new LedgerEntry
        {
            MerchantId = merchantId,
            Kind = LedgerEntryKind.Adjustment,
            Amount = amount,
            EntryDate = entryDate,
            Note = why
        };
    }

    /// <summary>
    /// The courier wrote this adjustment by hand: the merchant is emailed what it is and why. An online payment's
    /// credit is not, since its own receipt says it.
    /// </summary>
    public void TellMerchant()
    {
        if (Kind != LedgerEntryKind.Adjustment)
        {
            throw new InvalidOperationException("Only an adjustment is told to the merchant on its own.");
        }

        Raise(new AdjustmentWritten(this));
    }

    /// <summary>The payout <paramref name="payout"/> takes the line. A line is paid out once.</summary>
    internal void PayIn(Payout payout)
    {
        if (IsPaidOut)
        {
            throw new InvalidOperationException("This ledger line is already paid out.");
        }

        Payout = payout;
    }

    /// <summary>Its payout was cancelled before it was sent: the line waits for the next one again.</summary>
    internal void Release()
    {
        Payout = null;
        PayoutId = null;
    }
}

/// <summary>The courier credited or charged a merchant by hand: the merchant is emailed what and why.</summary>
public sealed record AdjustmentWritten(LedgerEntry Entry) : IDomainEvent;
