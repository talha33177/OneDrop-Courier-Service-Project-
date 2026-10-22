using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Html;
using Application.Payments.MerchantPayments;
using Application.Pricing.DeliveryOptions;
using Domain.Common;
using Domain.Delivery;
using Domain.Merchants;
using Domain.Parcels;
using Domain.Payments;
using Domain.Pricing;

namespace Web.Display;

/// <summary>
/// The words and colours every page uses for statuses, so a merchant, hub staff, a rider and an admin read the same thing
/// for the same status instead of the enum's name.
/// </summary>
public static class Statuses
{
    public static IHtmlContent Badge(ParcelStatus status)
    {
        var tone = status switch
        {
            ParcelStatus.Pending => "pending",
            ParcelStatus.PickedUp or ParcelStatus.AtHub or ParcelStatus.InTransit => "transit",
            ParcelStatus.OutForDelivery => "out",
            ParcelStatus.OnHold => "hold",
            ParcelStatus.Delivered or ParcelStatus.PartlyDelivered => "done",
            ParcelStatus.Returning or ParcelStatus.Returned => "return",
            _ => "cancel"
        };

        return Pill(tone, status.DisplayName());
    }

    /// <summary>A problem a hub flagged, with a flag: amber while in review, red when exceptional.</summary>
    public static IHtmlContent Badge(ParcelIssue issue)
    {
        var tone = issue == ParcelIssue.InReview ? "pending" : "return";

        return new HtmlString(
            $"""<span class="badge badge-{tone} plain">{Icons.Get("flag")}{WebUtility.HtmlEncode(issue.DisplayName())}</span>""");
    }

    public static IHtmlContent Badge(MerchantStatus status)
    {
        return status switch
        {
            MerchantStatus.Pending => Pill("pending", "Awaiting approval"),
            MerchantStatus.Active => Pill("done", "Active"),
            _ => Pill("return", "Suspended")
        };
    }

    public static IHtmlContent Badge(PickupStatus status)
    {
        return status switch
        {
            PickupStatus.Scheduled => Pill("pending", "Waiting for a rider"),
            PickupStatus.Assigned => Pill("out", "Rider assigned"),
            PickupStatus.Completed => Pill("done", "Picked up"),
            _ => Pill("cancel", "Called off")
        };
    }

    /// <summary>
    /// The tags a parcel carries in lists, labels and the rider's app: its speed and what is inside, each unless it is
    /// the courier's default. Nothing for a regular parcel of general goods.
    /// </summary>
    public static IHtmlContent Tags(DeliveryTags tags)
    {
        var html = new List<string>();
        if (tags.Speed is not null)
        {
            html.Add(Tag("speed", tags.SpeedIcon, tags.Speed));
        }

        if (tags.Contents is not null)
        {
            html.Add(Tag("contents", tags.ContentsIcon, tags.Contents));
        }

        return new HtmlString(string.Concat(html));
    }

    /// <summary>The day a pickup collects, as merchants read it: "today", "tomorrow", "on Sat 11 Oct".</summary>
    public static string PickupDay(DateOnly day, DateOnly today)
    {
        return day <= today ? "today" : day == today.AddDays(1) ? "tomorrow" : $"on {day:ddd d MMM}";
    }

    /// <summary>A time of day as people read it: "3:30 pm", "3 pm".</summary>
    public static string Clock(TimeOnly time)
    {
        return time.ToString(time.Minute == 0 ? "h tt" : "h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant();
    }

    /// <summary>
    /// When a parcel booked now is collected, in a sentence's middle: "with today's pickup (the truck leaves the hub at
    /// 3:30 pm)", "with tomorrow's pickup".
    /// </summary>
    public static string PickupWith(DateOnly day, DateOnly today, TimeOnly? leavesAt)
    {
        var when = PickupDay(day, today);
        var pickup = when is "today" or "tomorrow" ? $"with {when}'s pickup" : $"with the pickup {when}";

        return leavesAt is { } time ? $"{pickup} (the truck leaves the hub at {Clock(time)})" : pickup;
    }

    private static string Tag(string kind, string icon, string text)
    {
        return $"""<span class="tag tag-{kind}">{Icons.Named(icon)}{WebUtility.HtmlEncode(text)}</span>""";
    }

    public static IHtmlContent Badge(ParcelRequestStatus status)
    {
        return status switch
        {
            ParcelRequestStatus.Open => Pill("pending", "Waiting for an answer"),
            ParcelRequestStatus.Approved => Pill("done", "Approved"),
            _ => Pill("return", "Refused")
        };
    }

    /// <summary>What a request asks for, in words: "Cancel" or "Cash ৳1,250 to ৳900".</summary>
    public static IHtmlContent Asked(ParcelRequestKind kind, decimal codAmount, decimal? newCodAmount)
    {
        return kind == ParcelRequestKind.Cancel
            ? new HtmlString("Cancel and bring it back")
            : new HtmlContentBuilder()
                .AppendHtml("Cash on delivery ")
                .AppendHtml(Money.Taka(codAmount))
                .AppendHtml(" to ")
                .AppendHtml(Money.Taka(newCodAmount));
    }

    public static IHtmlContent Badge(ReturnListStatus status, bool toMerchant = false)
    {
        return status switch
        {
            ReturnListStatus.Out => Pill("out", "With the rider"),
            ReturnListStatus.HandedOver => Pill("pending", toMerchant ? "Please confirm you got them" : "Waiting for the merchant"),
            ReturnListStatus.Confirmed => Pill("done", "Received"),
            _ => Pill("return", "Not handed over")
        };
    }

    public static IHtmlContent Badge(BagStatus status)
    {
        return status switch
        {
            BagStatus.Open => Pill("pending", "Filling"),
            BagStatus.Sealed => Pill("transit", "On the truck"),
            BagStatus.Arrived => Pill("hold", "Being checked"),
            _ => Pill("done", "Closed")
        };
    }

    public static IHtmlContent Badge(CashDepositStatus status)
    {
        return status switch
        {
            CashDepositStatus.Recorded => Pill("pending", "Waiting for finance"),
            CashDepositStatus.Confirmed => Pill("done", "Confirmed"),
            _ => Pill("return", "Refused")
        };
    }

    public static IHtmlContent Badge(RunStatus status)
    {
        return status == RunStatus.Open ? Pill("out", "Open") : Pill("done", "Closed");
    }

    /// <summary>
    /// A payout's state; <paramref name="stuck"/> marks one the gateway has refused and the run keeps retrying. The
    /// courier reads what it did (paid, processing); the merchant reads what it gets (received, on its way).
    /// </summary>
    public static IHtmlContent Badge(PayoutStatus status, bool stuck = false, bool toMerchant = false)
    {
        return status switch
        {
            PayoutStatus.Paid => Pill("done", toMerchant ? "Received" : "Paid"),
            PayoutStatus.Cancelled => Pill("cancel", "Cancelled"),
            _ when stuck => Pill("return", toMerchant ? "Delayed, we are retrying" : "Refused, retrying"),
            _ => Pill("pending", toMerchant ? "On its way" : "Processing")
        };
    }

    /// <summary>A merchant's online payment of what it owed.</summary>
    public static IHtmlContent Badge(OnlinePaymentStatus status)
    {
        return status switch
        {
            OnlinePaymentStatus.Paid => Pill("done", "Paid"),
            OnlinePaymentStatus.Failed => Pill("cancel", "Not paid"),
            OnlinePaymentStatus.Review => Pill("hold", "Being checked"),
            OnlinePaymentStatus.Refunded => Pill("return", "Refunded"),
            OnlinePaymentStatus.RefundDue => Pill("return", "Refund due"),
            _ => Pill("pending", "Waiting")
        };
    }

    public static IHtmlContent Badge(AttemptOutcome? outcome)
    {
        return outcome switch
        {
            AttemptOutcome.Delivered => Pill("done", "Delivered"),
            AttemptOutcome.PartlyDelivered => Pill("done", "Partly delivered"),
            AttemptOutcome.Hold => Pill("hold", "On hold"),
            AttemptOutcome.Refused => Pill("return", "Refused"),
            _ => Pill("out", "With rider")
        };
    }

    /// <summary>A payout account in words: "bKash 01711000009", or "Bank 1501203456789".</summary>
    public static string PayoutAccount(PayoutMethod method, string? number)
    {
        return $"{method.DisplayName()} {(method == PayoutMethod.Bank ? number : Money.Phone(number))}";
    }

    public static string Name(LedgerEntryKind kind)
    {
        return kind.DisplayName();
    }

    /// <summary>
    /// The day a parcel on its way should be delivered by: quiet while ahead, amber on the day, red once late. Nothing
    /// for a parcel with no due day or no longer on its way.
    /// </summary>
    public static IHtmlContent Due(DateOnly? dueOn, ParcelStatus status, DateOnly today)
    {
        if (dueOn is not { } due || !ParcelStatuses.ToDeliver.Contains(status))
        {
            return HtmlString.Empty;
        }

        var (tone, text) = due < today ? ("late", $"Late, due {due:ddd d MMM}")
            : due == today ? ("today", "Due today")
            : ("", $"Due {due:ddd d MMM}");

        return new HtmlString($"""<span class="due {tone}">{WebUtility.HtmlEncode(text)}</span>""");
    }

    /// <summary>A promised delivery time: "Same day", "Next day", "3 days"; "No set time" when none is promised.</summary>
    public static string Promise(int? days)
    {
        return days switch
        {
            null => "No set time",
            0 => "Same day",
            1 => "Next day",
            _ => $"{days} days"
        };
    }

    /// <summary>How soon after pickup: "the same day as pickup", "the day after pickup", "within 3 days of pickup".</summary>
    public static string AfterPickup(int days)
    {
        return days switch
        {
            0 => "the same day as pickup",
            1 => "the day after pickup",
            _ => $"within {days} days of pickup"
        };
    }

    public static string Icon(Vehicle vehicle)
    {
        return vehicle switch
        {
            Vehicle.Bicycle => "bike",
            Vehicle.Motorbike => "motorbike",
            _ => "truck"
        };
    }

    private static HtmlString Pill(string tone, string text)
    {
        return new HtmlString($"""<span class="badge badge-{tone}">{WebUtility.HtmlEncode(text)}</span>""");
    }
}

/// <summary>
/// Taka amounts and phone numbers as people read them. Returned as HTML so the ৳ sign is written as it is; Razor would
/// otherwise encode it inside a C# expression.
/// </summary>
public static class Money
{
    public static IHtmlContent Taka(decimal amount)
    {
        var text = amount < 0
            ? "−৳" + (-amount).ToString("N0", CultureInfo.InvariantCulture)
            : "৳" + amount.ToString("N0", CultureInfo.InvariantCulture);

        return new HtmlString($"""<span class="amount"><span class="amount-figure">{text}</span></span>""");
    }

    public static IHtmlContent Taka(decimal? amount)
    {
        return amount is { } value ? Taka(value) : new HtmlString("""<span class="faint">—</span>""");
    }

    /// <summary>A stored E.164 number in the local form people dial: 01712345678.</summary>
    public static string Phone(string? e164)
    {
        return PhoneNumber.Parse(e164) is { IsSuccess: true } parsed ? parsed.Value.Local : e164 ?? "";
    }

    public static string Kg(int grams)
    {
        return Weight.Kg(grams);
    }
}
