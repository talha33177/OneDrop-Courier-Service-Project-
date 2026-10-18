using Domain.Common;

namespace Domain.Parcels;

/// <summary>Stored as TINYINT. Never renumber a value that has been saved.</summary>
public enum ParcelRequestKind : byte
{
    /// <summary>Stop the delivery and bring the parcel back; it is charged as a return.</summary>
    Cancel = 1,

    /// <summary>Collect a different cash on delivery.</summary>
    ChangeCod = 2
}

/// <summary>Stored as TINYINT. Never renumber a value that has been saved.</summary>
public enum ParcelRequestStatus : byte
{
    Open = 1,

    /// <summary>The courier agreed and the parcel was changed. Final.</summary>
    Approved = 2,

    /// <summary>The courier said no, and why. Final.</summary>
    Refused = 3
}

/// <summary>
/// A merchant asks the courier about a parcel it already has: to cancel it, which brings it back as a return, or to
/// collect a different cash on delivery. The courier approves, which changes the parcel there and then, or refuses
/// with a reason. One open request per parcel. Before pickup the merchant edits or cancels the parcel itself, so a
/// request is for a parcel on its way. Carries <see cref="MerchantId"/>, so a merchant sees only its own.
/// </summary>
public class ParcelRequest : TenantEntity, IMerchantOwned
{
    private ParcelRequest()
    {
    }

    public long MerchantId { get; private set; }

    public long ParcelId { get; private set; }

    public ParcelRequestKind Kind { get; private set; }

    /// <summary>The cash on delivery when the merchant asked.</summary>
    public decimal CodAmount { get; private set; }

    /// <summary>The cash on delivery asked for; set for a change only.</summary>
    public decimal? NewCodAmount { get; private set; }

    public string Reason { get; private set; } = "";

    /// <summary>The merchant's login that asked.</summary>
    public long? RequestedById { get; private set; }

    public ParcelRequestStatus Status { get; private set; }

    /// <summary>What the courier said: why it refused, or a note with its approval.</summary>
    public string? Answer { get; private set; }

    public DateTime? AnsweredOn { get; private set; }

    public long? AnsweredById { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static Result<ParcelRequest> Ask(Parcel parcel, ParcelRequestKind kind, decimal? newCodAmount, string? reason, long? userId)
    {
        if (!parcel.CanBeAskedAbout)
        {
            return Error.Conflict(
                "request.parcel",
                parcel.Status == ParcelStatus.Pending
                    ? $"{parcel.TrackingCode} has not been picked up yet: edit or cancel it yourself."
                    : $"{parcel.TrackingCode} is {parcel.Status.DisplayName().ToLowerInvariant()} and can no longer change.");
        }

        if (!Enum.IsDefined(kind))
        {
            return Error.Validation("request.kind", "Choose to cancel the parcel or to change its cash on delivery.");
        }

        var why = reason.NullIfBlank();
        if (why is null || why.Length > 200)
        {
            return Error.Validation("request.reason", "Say why, at most 200 characters.");
        }

        if (kind == ParcelRequestKind.ChangeCod && parcel.PaidOnline is { } paid)
        {
            return Error.Conflict(
                "request.cod.paid",
                $"The recipient already paid {parcel.TrackingCode}'s ৳{paid:N0} online, so its cash on delivery cannot change.");
        }

        if (kind == ParcelRequestKind.ChangeCod &&
            (newCodAmount is not { } amount || amount is < 0 or > Parcel.MaxCodAmount || amount == parcel.CodAmount))
        {
            return Error.Validation(
                "request.cod",
                $"Enter the new cash on delivery: another amount than ৳{parcel.CodAmount:N0}, between ৳0 and ৳{Parcel.MaxCodAmount:N0}.");
        }

        return new ParcelRequest
        {
            MerchantId = parcel.MerchantId,
            ParcelId = parcel.Id,
            Kind = kind,
            CodAmount = parcel.CodAmount,
            NewCodAmount = kind == ParcelRequestKind.ChangeCod ? newCodAmount : null,
            Reason = why,
            RequestedById = userId,
            Status = ParcelRequestStatus.Open
        };
    }

    /// <summary>
    /// The courier agrees: a cancellation sends the parcel back to the merchant, a change sets its new cash on delivery.
    /// When the parcel cannot take the change now (a cancellation while a rider has it, say) nothing changes and the
    /// request stays open.
    /// </summary>
    public Result Approve(Parcel parcel, string? answer, long? userId, DateTime now)
    {
        if (parcel.Id != ParcelId)
        {
            throw new InvalidOperationException("Approve a request with its own parcel.");
        }

        var answered = Answered(answer);
        if (answered.IsFailure)
        {
            return answered;
        }

        var applied = Kind == ParcelRequestKind.Cancel
            ? parcel.RequestReturn($"Cancelled by the merchant: {Reason}")
            : parcel.ChangeCod(NewCodAmount!.Value, $"the merchant asked: {Reason}");
        if (applied.IsFailure)
        {
            return applied;
        }

        Close(ParcelRequestStatus.Approved, answer, userId, now);

        return Result.Success();
    }

    /// <summary>The courier says no, and why; the parcel stays as it is.</summary>
    public Result Refuse(string? answer, long? userId, DateTime now)
    {
        var answered = Answered(answer);
        if (answered.IsFailure)
        {
            return answered;
        }

        if (answer.NullIfBlank() is null)
        {
            return Error.Validation("request.answer", "Say why the request is refused, so the merchant knows.");
        }

        Close(ParcelRequestStatus.Refused, answer, userId, now);

        return Result.Success();
    }

    private Result Answered(string? answer)
    {
        if (Status != ParcelRequestStatus.Open)
        {
            return Error.Conflict("request.answered", "This request has already been answered.");
        }

        return answer?.Trim().Length > 200
            ? Error.Validation("request.answer", "The answer is at most 200 characters.")
            : Result.Success();
    }

    private void Close(ParcelRequestStatus status, string? answer, long? userId, DateTime now)
    {
        Status = status;
        Answer = answer.NullIfBlank();
        AnsweredOn = now;
        AnsweredById = userId;
        Raise(new ParcelRequestAnswered(this));
    }
}

/// <summary>The courier approved or refused a merchant's request about a parcel: the merchant is told by email.</summary>
public sealed record ParcelRequestAnswered(ParcelRequest Request) : IDomainEvent;

public static class ParcelRequestKinds
{
    /// <summary>How the request is written for people: "Cancel" or "Change the cash on delivery".</summary>
    public static string DisplayName(this ParcelRequestKind kind)
    {
        return kind == ParcelRequestKind.Cancel ? "Cancel" : "Change the cash on delivery";
    }
}
