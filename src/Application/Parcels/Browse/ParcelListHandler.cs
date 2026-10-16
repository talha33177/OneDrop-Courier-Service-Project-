using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Application.Pricing.DeliveryOptions;
using Domain.Common;
using Domain.Parcels;
using Domain.Pricing;

namespace Application.Parcels.Browse;

/// <summary>
/// The tabs a parcel list is cut into: each a group of statuses, and last the problems a hub flagged, which a parcel
/// carries whatever its status.
/// </summary>
public enum ParcelTab
{
    All,
    Pending,
    InTransit,
    OutForDelivery,
    OnHold,
    Delivered,
    PartlyDelivered,
    Returns,
    Cancelled,
    InReview,
    Exceptional
}

public sealed record ParcelQuery
{
    public ParcelTab Tab { get; init; }

    /// <summary>A tracking code, the merchant's reference, the recipient's name or phone.</summary>
    public string? Search { get; init; }

    /// <summary>Courier staff only: one merchant's parcels.</summary>
    public long? MerchantId { get; init; }

    /// <summary>Courier staff only: parcels collected or delivered by one hub.</summary>
    public long? HubId { get; init; }

    /// <summary>Booked on or after this day (the tenant's date).</summary>
    public DateOnly? From { get; init; }

    /// <summary>Booked on or before this day.</summary>
    public DateOnly? To { get; init; }

    /// <summary>Only parcels still on their way after the day they were due.</summary>
    public bool Late { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 25;
}

/// <summary>A parcel as a list shows it. Times are the tenant's; <see cref="Late"/> is as of today.</summary>
public sealed record ParcelRow(
    string TrackingCode,
    string? MerchantReference,
    string Merchant,
    string RecipientName,
    string RecipientPhone,
    string Area,
    string DeliveryHub,
    ParcelStatus Status,
    decimal CodAmount,
    decimal? CollectedAmount,
    decimal DeliveryCharge,
    int Attempts,
    DateTime Booked,
    DateOnly? DueOn)
{
    public bool Late { get; init; }

    /// <summary>A problem a hub flagged on the parcel, if any.</summary>
    public ParcelIssue? Issue { get; init; }

    public DeliveryTags Tags { get; init; } = new(null, "", null, "", null, false);
}

public sealed record ParcelList(Page<ParcelRow> Page, IReadOnlyDictionary<ParcelTab, int> Counts);

/// <summary>A parcel as an export file holds it: everything a merchant's own books need. Times are the tenant's.</summary>
public sealed record ParcelExportRow(
    string TrackingCode,
    string? MerchantReference,
    string RecipientName,
    string RecipientPhone,
    string RecipientAddress,
    string Area,
    ParcelStatus Status,
    ParcelIssue? Issue,
    decimal CodAmount,
    decimal? CollectedAmount,
    decimal DeliveryCharge,
    decimal CodCharge,
    decimal TotalCharge,
    int WeightGrams,
    DateTime Booked,
    DateTime? Closed,
    DateOnly? DueOn);

/// <summary>
/// Lists parcels with tabs, search, date range, late ones only and paging. A merchant sees only its own (the merchant
/// filter); courier staff see every merchant's and can narrow to one merchant or hub.
/// </summary>
public class ParcelListHandler(IAppDbContext db, ITenantContext tenantContext, TimeProvider time)
{
    public const int MaxPageSize = 100;

    /// <summary>The statuses a tab holds; every status for All and for the tabs of flagged problems.</summary>
    public static ParcelStatus[] StatusesOf(ParcelTab tab)
    {
        return tab switch
        {
            ParcelTab.Pending => [ParcelStatus.Pending],
            ParcelTab.InTransit => [ParcelStatus.PickedUp, ParcelStatus.AtHub, ParcelStatus.InTransit],
            ParcelTab.OutForDelivery => [ParcelStatus.OutForDelivery],
            ParcelTab.OnHold => [ParcelStatus.OnHold],
            ParcelTab.Delivered => [ParcelStatus.Delivered],
            ParcelTab.PartlyDelivered => [ParcelStatus.PartlyDelivered],
            ParcelTab.Returns => [ParcelStatus.Returning, ParcelStatus.Returned],
            ParcelTab.Cancelled => [ParcelStatus.Cancelled],
            _ => Enum.GetValues<ParcelStatus>()
        };
    }

    /// <summary>The problem a tab of flagged parcels holds; null for the tabs of statuses.</summary>
    public static ParcelIssue? IssueOf(ParcelTab tab)
    {
        return tab switch
        {
            ParcelTab.InReview => ParcelIssue.InReview,
            ParcelTab.Exceptional => ParcelIssue.Exceptional,
            _ => null
        };
    }

    public async Task<ParcelList> ListAsync(ParcelQuery query, CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var today = tenant.Today(time.GetUtcNow().UtcDateTime);
        var parcels = Filter(db.Parcels.AsNoTracking(), query, tenant, today);

        var byStatus = await parcels
            .GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Status, g => g.Count, cancellationToken);
        var byIssue = await parcels
            .Where(p => p.Issue != null)
            .GroupBy(p => p.Issue)
            .Select(g => new { Issue = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Issue!.Value, g => g.Count, cancellationToken);
        var counts = Enum.GetValues<ParcelTab>()
            .ToDictionary(
                tab => tab,
                tab => IssueOf(tab) is { } issue
                    ? byIssue.GetValueOrDefault(issue)
                    : StatusesOf(tab).Sum(status => byStatus.GetValueOrDefault(status)));

        var statuses = StatusesOf(query.Tab);
        var flagged = IssueOf(query.Tab);
        var size = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var number = Math.Max(1, query.Page);
        var inTab = flagged is null ? parcels.Where(p => statuses.Contains(p.Status)) : parcels.Where(p => p.Issue == flagged);
        var rows = await (
            from parcel in inTab
            join merchant in db.Merchants on parcel.MerchantId equals merchant.Id
            join area in db.Areas on parcel.AreaId equals area.Id
            join hub in db.Hubs on parcel.DeliveryHubId equals hub.Id
            orderby parcel.Id descending
            select new ParcelRow(
                parcel.TrackingCode,
                parcel.MerchantReference,
                merchant.Name,
                parcel.RecipientName,
                parcel.RecipientPhone,
                area.Name,
                hub.Name,
                parcel.Status,
                parcel.CodAmount,
                parcel.CollectedAmount,
                parcel.DeliveryCharge,
                parcel.Attempts,
                parcel.Created,
                parcel.DueOn)
            {
                Issue = parcel.Issue,
                Tags = new DeliveryTags(
                    parcel.Speed!.IsDefault ? null : parcel.Speed.Name,
                    parcel.Speed.Icon,
                    parcel.ProductType!.IsDefault ? null : parcel.ProductType.Name,
                    parcel.ProductType.Icon,
                    parcel.ProductType.Handling,
                    parcel.Speed.GoesFirst || parcel.ProductType.GoesFirst)
            })
            .Skip((number - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);

        return new ParcelList(
            new Page<ParcelRow>(
                [
                    .. rows.Select(row => row with
                    {
                        Booked = tenant.Local(row.Booked),
                        Late = row.DueOn < today && ParcelStatuses.ToDeliver.Contains(row.Status)
                    })
                ],
                counts[query.Tab],
                number,
                size),
            counts);
    }

    /// <summary>The most parcels one export file holds.</summary>
    public const int MaxExport = 10_000;

    /// <summary>
    /// Every parcel of the tab and filters, newest first, up to <see cref="MaxExport"/>, with the charges worked out as
    /// the parcel page shows them; the query's page is ignored.
    /// </summary>
    public async Task<IReadOnlyList<ParcelExportRow>> ExportAsync(ParcelQuery query, CancellationToken cancellationToken = default)
    {
        var tenant = tenantContext.Require();
        var today = tenant.Today(time.GetUtcNow().UtcDateTime);
        var statuses = StatusesOf(query.Tab);
        var flagged = IssueOf(query.Tab);
        var parcels = Filter(db.Parcels.AsNoTracking(), query, tenant, today);
        parcels = flagged is null ? parcels.Where(p => statuses.Contains(p.Status)) : parcels.Where(p => p.Issue == flagged);
        var rows = await (
            from parcel in parcels
            join area in db.Areas on parcel.AreaId equals area.Id
            orderby parcel.Id descending
            select new { Parcel = parcel, Area = area.Name })
            .Take(MaxExport)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(row => new ParcelExportRow(
                row.Parcel.TrackingCode,
                row.Parcel.MerchantReference,
                row.Parcel.RecipientName,
                row.Parcel.RecipientPhone,
                row.Parcel.RecipientAddress,
                row.Area,
                row.Parcel.Status,
                row.Parcel.Issue,
                row.Parcel.CodAmount,
                row.Parcel.CollectedAmount,
                row.Parcel.DeliveryCharge,
                row.Parcel.Status switch
                {
                    ParcelStatus.Delivered or ParcelStatus.PartlyDelivered => row.Parcel.CodCharge ?? 0,
                    ParcelStatus.Returned or ParcelStatus.Cancelled => 0,
                    _ => row.Parcel.Charges.CodChargeOn(row.Parcel.CodAmount)
                },
                row.Parcel.TotalCharge,
                row.Parcel.BilledWeightGrams,
                tenant.Local(row.Parcel.Created),
                row.Parcel.ClosedOn is { } closed ? tenant.Local(closed) : null,
                row.Parcel.DueOn))
        ];
    }

    private static IQueryable<Parcel> Filter(IQueryable<Parcel> parcels, ParcelQuery query, TenantInfo tenant, DateOnly today)
    {
        if (query.Late)
        {
            parcels = parcels.Where(p => p.DueOn < today && ParcelStatuses.ToDeliver.Contains(p.Status));
        }

        if (query.MerchantId is { } merchantId)
        {
            parcels = parcels.Where(p => p.MerchantId == merchantId);
        }

        if (query.HubId is { } hubId)
        {
            parcels = parcels.Where(p => p.PickupHubId == hubId || p.DeliveryHubId == hubId);
        }

        if (query.From is { } from)
        {
            var start = tenant.StartUtc(from);
            parcels = parcels.Where(p => p.Created >= start);
        }

        if (query.To is { } to)
        {
            var end = tenant.StartUtc(to.AddDays(1));
            parcels = parcels.Where(p => p.Created < end);
        }

        var search = query.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var phone = PhoneNumber.Parse(search);
            if (phone.IsSuccess)
            {
                var value = phone.Value.Value;
                parcels = parcels.Where(p => p.RecipientPhone == value);
            }
            else
            {
                parcels = parcels.Where(p =>
                    p.TrackingCode == search ||
                    p.MerchantReference == search ||
                    p.RecipientName.Contains(search));
            }
        }

        return parcels;
    }
}
