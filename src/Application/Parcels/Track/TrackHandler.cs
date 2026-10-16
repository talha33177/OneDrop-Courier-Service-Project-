using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Domain.Common;
using Domain.Parcels;

namespace Application.Parcels.Track;

public sealed record TrackingStep(DateTime When, ParcelStatus Status, string Note, string? Hub);

/// <summary>
/// What anyone with a tracking code sees: the status, who sent it, the destination area, the day it should arrive by
/// (while it is on its way and a time was promised) and the history. Never the recipient's phone or address, the cash
/// on delivery or the charges.
/// </summary>
public sealed record TrackingView(
    string TrackingCode,
    ParcelStatus Status,
    string Merchant,
    string Area,
    string City,
    DateTime Booked,
    DateOnly? ExpectedBy,
    IReadOnlyList<TrackingStep> Steps)
{
    /// <summary>The recipient may pay the cash on delivery online now (the amount shows only on the pay page).</summary>
    public bool PayOnline { get; init; }

    /// <summary>The cash on delivery was paid online: nothing is collected at the door.</summary>
    public bool PaidOnline { get; init; }
}

/// <summary>The public tracking page, open to everyone on the courier's site.</summary>
public class TrackHandler(IAppDbContext db, ITenantContext tenantContext, IPaymentGateway gateway)
{
    public static readonly Error NotFound =
        Error.NotFound("track.notFound", "We could not find a parcel with that tracking code. Check it and try again.");

    public async Task<Result<TrackingView>> GetAsync(string? trackingCode, CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var code = trackingCode?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code))
        {
            return NotFound;
        }

        // Public: the merchant filter only ever applies to a merchant caller, and a tracking code is for anyone holding it
        var found = await (
            from parcel in db.Parcels
            join merchant in db.Merchants on parcel.MerchantId equals merchant.Id
            join area in db.Areas on parcel.AreaId equals area.Id
            join zone in db.Zones on area.ZoneId equals zone.Id
            where parcel.TrackingCode == code
            select new { parcel.Id, parcel.TrackingCode, parcel.Status, Merchant = merchant.Name, Area = area.Name, zone.City, parcel.Created, parcel.DueOn, Parcel = parcel })
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        if (found is null)
        {
            return NotFound;
        }

        var steps = await (
            from e in db.ParcelEvents
            where e.ParcelId == found.Id
            orderby e.Id descending
            select new TrackingStep(e.Created, e.Status, e.Note, db.Hubs.Where(h => h.Id == e.HubId).Select(h => h.Name).FirstOrDefault()))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return new TrackingView(
            found.TrackingCode,
            found.Status,
            found.Merchant,
            found.Area,
            found.City,
            tenant.Local(found.Created),
            ParcelStatuses.ToDeliver.Contains(found.Status) ? found.DueOn : null,
            // The public sees each step by its status; staff notes can name amounts or reasons meant for the merchant. A hold
            // keeps its note, which tells the recipient what the rider needs from them
            [.. steps.Select(step => step with
            {
                When = tenant.Local(step.When),
                Note = step.Status == ParcelStatus.OnHold ? step.Note : step.Status.DisplayName()
            })])
        {
            PayOnline = gateway.IsAvailable && found.Parcel.CanBePaidOnline().IsSuccess,
            PaidOnline = found.Parcel.PaidOnline is not null
        };
    }
}
