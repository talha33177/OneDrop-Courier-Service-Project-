using System.Text.Json.Serialization;
using Domain.Parcels;
using Domain.Pricing;

namespace Application.Parcels.CreateParcel;

/// <summary>
/// POST /api/v1/parcels, the merchant panel's booking form and each row of a bulk upload. The merchant is the caller
/// (from the API key or the sign-in); the tenant comes with it.
/// </summary>
public sealed record CreateParcelCommand
{
    /// <summary>The merchant's own order or invoice number, shown back in lists, labels and webhooks.</summary>
    public string? MerchantReference { get; init; }

    public string? RecipientName { get; init; }

    /// <summary>A Bangladeshi mobile number in any common spelling.</summary>
    public string? RecipientPhone { get; init; }

    public string? RecipientAddress { get; init; }

    /// <summary>Optional: the door pinned on a map, in degrees (23.810331), with <see cref="RecipientLongitude"/>.</summary>
    public decimal? RecipientLatitude { get; init; }

    public decimal? RecipientLongitude { get; init; }

    /// <summary>Give either AreaId or the Area name, from GET /api/v1/areas.</summary>
    public long? AreaId { get; init; }

    public string? Area { get; init; }

    /// <summary>Optional. Defaults to the merchant's default pickup point.</summary>
    public long? PickupPointId { get; init; }

    /// <summary>Cash to collect from the recipient. 0 when they have paid already.</summary>
    public decimal CodAmount { get; init; }

    public decimal WeightKg { get; init; }

    /// <summary>
    /// The speed's code from the courier's delivery types (regular, express and so on), where the route offers it.
    /// Left out, the courier's default.
    /// </summary>
    public string? Speed { get; init; }

    /// <summary>
    /// What is inside, by its code from the courier's delivery types (general, liquid, fragile, fresh-food, frozen
    /// and so on), where the route carries it. Left out, the courier's default.
    /// </summary>
    public string? ProductType { get; init; }

    public string? ItemDescription { get; init; }

    /// <summary>Instructions for the rider.</summary>
    public string? Note { get; init; }

    /// <summary>From the Idempotency-Key header, not the body.</summary>
    [JsonIgnore]
    public string? IdempotencyKey { get; init; }

    [JsonIgnore]
    public int WeightGrams => (int)Math.Round(WeightKg * 1000m, MidpointRounding.AwayFromZero);
}

/// <param name="DeliveryCharge">The weight's charge plus <see cref="SpeedCharge"/> and <see cref="ProductCharge"/>.</param>
/// <param name="TotalCharge">What the merchant pays if the parcel is delivered with the whole cash on delivery.</param>
/// <param name="PickupOn">The day the courier collects it: today, or the next day once the pickup van has been to its pickup point today.</param>
public sealed record CreateParcelResult(
    string TrackingCode,
    string? MerchantReference,
    ParcelStatus Status,
    string Area,
    string DeliveryHub,
    ServiceArea ServiceArea,
    decimal CodAmount,
    decimal DeliveryCharge,
    decimal CodCharge,
    decimal TotalCharge,
    DateTime Created,
    string Speed,
    string ProductType,
    decimal SpeedCharge,
    decimal ProductCharge,
    DateOnly? PickupOn)
{
    /// <summary>True when this is an earlier parcel returned for a repeated Idempotency-Key.</summary>
    public bool Replayed { get; init; }
}
