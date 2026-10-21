using Domain.Common;

namespace Domain.Payments;

/// <summary>Stored as TINYINT. Never renumber a value that has been saved.</summary>
public enum CashDepositStatus : byte
{
    /// <summary>The hub's cash desk paid it in; finance has still to find it in the bank.</summary>
    Recorded = 1,

    /// <summary>Finance found the money in the bank. Final.</summary>
    Confirmed = 2,

    /// <summary>Finance did not find it as recorded, and said why; the cash counts as the hub's again. Final.</summary>
    Refused = 3
}

/// <summary>
/// Cash a hub's cash desk paid into the courier's bank, with the bank's slip. The hub's cash in hand is the cash its
/// riders handed in at closing less what it deposited (refused deposits not counted), so a deposit is never more than
/// the hub holds. Finance confirms it once the money shows in the bank, or refuses it with a reason. Numbered DP-100001
/// from a database sequence.
/// </summary>
public class CashDeposit : TenantEntity
{
    public const int MaxBankLength = 100;
    public const int MaxSlipLength = 50;
    public const int MaxAnswerLength = 200;

    /// <summary>How long after paying in a deposit can still be recorded.</summary>
    public const int MaxDaysLate = 30;

    private CashDeposit()
    {
    }

    public long HubId { get; private set; }

    /// <summary>DP-100001, assigned by the database on insert; null until saved, so the sequence fills it.</summary>
    public string Number { get; private set; } = null!;

    public decimal Amount { get; private set; }

    /// <summary>The bank and branch it was paid in at.</summary>
    public string Bank { get; private set; } = "";

    /// <summary>The number on the bank's deposit slip.</summary>
    public string SlipNumber { get; private set; } = "";

    /// <summary>The day the cash was paid in, the courier's date.</summary>
    public DateOnly DepositedOn { get; private set; }

    public CashDepositStatus Status { get; private set; }

    /// <summary>The cash desk's login that recorded it.</summary>
    public long? RecordedById { get; private set; }

    public DateTime? AnsweredOn { get; private set; }

    /// <summary>Finance's login that confirmed or refused it.</summary>
    public long? AnsweredById { get; private set; }

    /// <summary>Why finance refused it.</summary>
    public string? Answer { get; private set; }

    /// <summary>Two people answering at once: one of them wins.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>
    /// The hub <paramref name="hubId"/> paid <paramref name="amount"/> of its <paramref name="cashInHand"/> into the bank
    /// on <paramref name="depositedOn"/>, by the login <paramref name="userId"/>.
    /// </summary>
    public static Result<CashDeposit> Record(
        long hubId,
        decimal amount,
        string? bank,
        string? slipNumber,
        DateOnly depositedOn,
        decimal cashInHand,
        DateOnly today,
        long? userId)
    {
        if (amount <= 0 || amount != decimal.Round(amount, 2))
        {
            return Error.Validation("deposit.amount", "Enter the amount paid in.");
        }

        if (amount > cashInHand)
        {
            return Error.Validation(
                "deposit.tooMuch",
                $"The hub holds ৳{cashInHand:N0} in cash, so it cannot have paid in ৳{amount:N0}. Check the amount on the slip.");
        }

        var paidAt = bank.NullIfBlank();
        if (paidAt is null || paidAt.Length > MaxBankLength)
        {
            return Error.Validation("deposit.bank", $"Enter the bank and branch, at most {MaxBankLength} characters.");
        }

        var slip = slipNumber.NullIfBlank()?.ToUpperInvariant();
        if (slip is null || slip.Length > MaxSlipLength)
        {
            return Error.Validation("deposit.slip", $"Enter the number on the deposit slip, at most {MaxSlipLength} characters.");
        }

        if (depositedOn > today || depositedOn < today.AddDays(-MaxDaysLate))
        {
            return Error.Validation("deposit.day", $"Enter the day it was paid in: today or in the last {MaxDaysLate} days.");
        }

        return new CashDeposit
        {
            HubId = hubId,
            Amount = amount,
            Bank = paidAt,
            SlipNumber = slip,
            DepositedOn = depositedOn,
            Status = CashDepositStatus.Recorded,
            RecordedById = userId
        };
    }

    /// <summary>Finance found the money in the bank.</summary>
    public Result Confirm(long? userId, DateTime now)
    {
        var open = Open();
        if (open.IsFailure)
        {
            return open;
        }

        Status = CashDepositStatus.Confirmed;
        AnsweredOn = now;
        AnsweredById = userId;

        return Result.Success();
    }

    /// <summary>Finance did not find the money as recorded; <paramref name="reason"/> says what the bank shows.</summary>
    public Result Refuse(string? reason, long? userId, DateTime now)
    {
        var open = Open();
        if (open.IsFailure)
        {
            return open;
        }

        var why = reason.NullIfBlank();
        if (why is null || why.Length > MaxAnswerLength)
        {
            return Error.Validation("deposit.reason", $"Say what the bank shows, at most {MaxAnswerLength} characters.");
        }

        Status = CashDepositStatus.Refused;
        AnsweredOn = now;
        AnsweredById = userId;
        Answer = why;

        return Result.Success();
    }

    private Result Open()
    {
        return Status == CashDepositStatus.Recorded
            ? Result.Success()
            : Error.Conflict("deposit.answered", $"Deposit {Number} has already been {(Status == CashDepositStatus.Confirmed ? "confirmed" : "refused")}.");
    }
}
