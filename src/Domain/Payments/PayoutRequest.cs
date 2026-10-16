using Domain.Common;
using Domain.Merchants;

namespace Domain.Payments;

/// <summary>Stored as TINYINT. Never renumber a value that has been saved.</summary>
public enum PayoutRequestStatus : byte
{
    /// <summary>Waiting for the courier's admin.</summary>
    Open = 1,

    /// <summary>The admin paid the balance at once, as <see cref="PayoutRequest.PayoutId"/>. Final.</summary>
    Paid = 2,

    /// <summary>The admin said no, and why; the balance goes out with the daily payouts as usual. Final.</summary>
    Refused = 3,

    /// <summary>The merchant took the request back. Final.</summary>
    Withdrawn = 4
}

/// <summary>
/// A merchant asks to be paid its balance now, today's cash too, instead of waiting for the next morning's payout. The
/// courier's admin decides: today's cash may still be with a rider whose run is not closed, and parcels still on their
/// way may come back and be charged, so a person looks before money leaves early. Paying sends the balance as it is
/// then, through the same payout the daily run makes. One open request per business; it carries
/// <see cref="MerchantId"/>, so a merchant sees only its own.
/// </summary>
public class PayoutRequest : TenantEntity, IMerchantOwned
{
    public const int MaxAnswerLength = 200;

    private PayoutRequest()
    {
    }

    public long MerchantId { get; private set; }

    /// <summary>The balance when the merchant asked; what is paid is the balance when the admin pays.</summary>
    public decimal AmountAsked { get; private set; }

    public PayoutRequestStatus Status { get; private set; }

    /// <summary>Why the admin refused; null otherwise.</summary>
    public string? Answer { get; private set; }

    public DateTime? AnsweredOn { get; private set; }

    /// <summary>The payout the admin made for it.</summary>
    public long? PayoutId { get; private set; }

    public bool IsOpen => Status == PayoutRequestStatus.Open;

    /// <summary>The admin answering while the merchant withdraws: one of them wins.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>
    /// <paramref name="merchant"/> asks to be paid <paramref name="balance"/>, its balance now: only when there is money
    /// to pay, an account to send it to, and the courier is not holding its payouts.
    /// </summary>
    public static Result<PayoutRequest> Ask(Merchant merchant, decimal balance)
    {
        if (merchant.ArePayoutsHeld)
        {
            return Error.Conflict("payoutRequest.held", $"Payouts are on hold: {merchant.PayoutHold}");
        }

        if (!merchant.HasPayoutAccount)
        {
            return Error.Validation("payoutRequest.account", "Add your payout account first, so there is somewhere to send the money.");
        }

        if (balance <= 0)
        {
            return Error.Validation("payoutRequest.nothing", "There is nothing to pay yet: no cash is waiting, or the charges come to more than it.");
        }

        return new PayoutRequest
        {
            MerchantId = merchant.Id,
            AmountAsked = balance,
            Status = PayoutRequestStatus.Open
        };
    }

    /// <summary>The admin paid it with <paramref name="payout"/>, the merchant's payout made just now.</summary>
    public Result Pay(Payout payout, DateTime now)
    {
        if (payout.MerchantId != MerchantId || payout.IsNew)
        {
            throw new InvalidOperationException("A request is paid by a saved payout of its own merchant.");
        }

        if (!IsOpen)
        {
            return NotOpen();
        }

        Status = PayoutRequestStatus.Paid;
        PayoutId = payout.Id;
        AnsweredOn = now;

        return Result.Success();
    }

    /// <summary>The admin says no, and why: the merchant reads it on its Payments page.</summary>
    public Result Refuse(string? answer, DateTime now)
    {
        if (!IsOpen)
        {
            return NotOpen();
        }

        var why = answer.NullIfBlank();
        if (why is null || why.Length > MaxAnswerLength)
        {
            return Error.Validation("payoutRequest.answer", $"Say why, in at most {MaxAnswerLength} characters: the merchant reads it.");
        }

        Status = PayoutRequestStatus.Refused;
        Answer = why;
        AnsweredOn = now;

        return Result.Success();
    }

    /// <summary>The merchant takes back its request before the admin answers.</summary>
    public Result Withdraw(DateTime now)
    {
        if (!IsOpen)
        {
            return NotOpen();
        }

        Status = PayoutRequestStatus.Withdrawn;
        AnsweredOn = now;

        return Result.Success();
    }

    private Error NotOpen()
    {
        return Error.Conflict(
            "payoutRequest.answered",
            Status switch
            {
                PayoutRequestStatus.Paid => "This request was paid already.",
                PayoutRequestStatus.Refused => "This request was refused already.",
                _ => "This request was withdrawn."
            });
    }
}
