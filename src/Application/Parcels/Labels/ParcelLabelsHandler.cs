using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Pricing.DeliveryOptions;
using Domain.Parcels;
using Domain.Pricing;

namespace Application.Parcels.Labels;

/// <summary>
/// What a parcel label shows: who it is from and to, the hub that delivers it, the cash to collect, and in large type
/// whether it is express and how to handle what is inside.
/// </summary>
public sealed record ParcelLabel(
    string TrackingCode,
    string? MerchantReference,
    string Merchant,
    string MerchantPhone,
    string RecipientName,
    string RecipientPhone,
    string RecipientAddress,
    string Area,
    string DeliveryHubCode,
    decimal CodAmount,
    int WeightGrams,
    string? ItemDescription,
    string? Note,
    DeliveryTags Tags);

/// <summary>
/// Labels to print and stick on parcels: the ones named, or every parcel still waiting for pickup, newest first. A
/// merchant prints only its own; hub staff can reprint any.
/// </summary>
public class ParcelLabelsHandler(IAppDbContext db)
{
    public const int MaxLabels = 200;

    public async Task<IReadOnlyList<ParcelLabel>> ListAsync(
        IReadOnlyCollection<string> trackingCodes,
        CancellationToken cancellationToken = default)
    {
        var codes = trackingCodes.Select(code => code.Trim().ToUpperInvariant()).Where(code => code.Length > 0).ToList();
        var parcels = codes.Count > 0
            ? db.Parcels.Where(p => codes.Contains(p.TrackingCode))
            : db.Parcels.Where(p => p.Status == ParcelStatus.Pending);

        return await (
            from parcel in parcels
            join merchant in db.Merchants on parcel.MerchantId equals merchant.Id
            join area in db.Areas on parcel.AreaId equals area.Id
            join hub in db.Hubs on parcel.DeliveryHubId equals hub.Id
            orderby parcel.Id descending
            select new ParcelLabel(
                parcel.TrackingCode,
                parcel.MerchantReference,
                merchant.Name,
                merchant.ContactPhone,
                parcel.RecipientName,
                parcel.RecipientPhone,
                parcel.RecipientAddress,
                area.Name,
                hub.Code,
                parcel.CodAmount,
                parcel.WeightGrams,
                parcel.ItemDescription,
                parcel.Note,
                new DeliveryTags(
                    parcel.Speed!.IsDefault ? null : parcel.Speed.Name,
                    parcel.Speed.Icon,
                    parcel.ProductType!.IsDefault ? null : parcel.ProductType.Name,
                    parcel.ProductType.Icon,
                    parcel.ProductType.Handling,
                    parcel.Speed.GoesFirst || parcel.ProductType.GoesFirst)))
            .Take(MaxLabels)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }
}
