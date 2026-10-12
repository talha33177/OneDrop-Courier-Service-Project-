using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Application.Abstractions;
using Application.Common;
using Domain.Common;
using Domain.Delivery;
using Domain.Merchants;
using Domain.Parcels;
using Domain.Payments;
using Domain.Pricing;
using Infrastructure.Persistence;

namespace Infrastructure.Seeding;

/// <summary>
/// Development-only sample parcels for a courier that has none, so every panel has something to show: parcels waiting
/// for pickup, at hubs, in transit, out with riders, delivered, held and returned, made with the same domain methods
/// the screens use, plus two pickup requests. The riders' runs are left open so the hub can close them.
/// </summary>
internal sealed class DemoActivity(
    AppDbContext db,
    ITenantContext tenantContext,
    TimeProvider time,
    ILogger<DemoActivity> logger)
{
    private static readonly Recipient[] Recipients =
    [
        new("Rahim Uddin", "01811000101", "Mirpur 2", "House 22, Road 4, Block C", 1250, 500, "Cotton panjabi"),
        new("Karima Begum", "01811000102", "Gulshan 2", "Flat 5B, House 9, Road 71", 2400, 1200, "Bluetooth speaker"),
        new("Arif Hossain", "01811000103", "Dhanmondi", "House 15, Road 8A", 890, 300, "Face wash set"),
        new("Sabina Yasmin", "01811000104", "Uttara Sector 4", "House 3, Road 12", 1800, 800, "Kurti"),
        new("Mizanur Rahman", "01811000105", "Agrabad", "Lane 2, Agrabad C/A", 3200, 2500, "Rice cooker"),
        new("Nasrin Sultana", "01811000106", "Savar", "Bank Colony, Savar", 650, 400, "Lipstick set"),
        new("Jahid Khan", "01811000107", "Sylhet Sadar", "Zindabazar Road", 1500, 1000, "Phone case"),
        new("Laila Karim", "01811000108", "Mirpur 10", "House 4, Road 1, Section 10", 0, 500, "Book, paid online"),
        new("Shirin Akhter", "01811000109", "Banani", "House 2, Road 11", 4500, 1500, "Smart watch"),
        new("Tahmina Akter", "01811000110", "Pallabi", "House 77, Pallabi", 1100, 600, "Two sarees"),
        new("Rashed Khan", "01811000111", "Mohammadpur", "12 Tajmahal Road", 2000, 900, "Headphones"),
        new("Parveen Sultana", "01811000112", "Kalabagan", "Lake Circus, Kalabagan", 750, 500, "Moisturiser"),
        new("Imtiaz Ali", "01811000113", "Panchlaish", "O.R. Nizam Road", 950, 700, "Wall hanging"),
        new("Rokeya Begum", "01811000114", "Narayanganj Sadar", "Chashara, Narayanganj", 1350, 1100, "Bedsheet")
    ];

    // Merchant (by its order in the seeder), recipient, and how far the parcel has got
    private static readonly (int Merchant, int Recipient, Stage Stage)[] Plan =
    [
        (0, 0, Stage.Delivered), (1, 1, Stage.Delivered), (2, 2, Stage.Delivered), (0, 3, Stage.Delivered),
        (3, 4, Stage.Delivered), (0, 9, Stage.PartlyDelivered), (1, 8, Stage.OutForDelivery), (0, 7, Stage.OutForDelivery),
        (2, 10, Stage.OutForDelivery), (2, 11, Stage.OnHold), (0, 12, Stage.Returning), (1, 0, Stage.Returned),
        (0, 5, Stage.InTransit), (1, 6, Stage.InTransit), (2, 13, Stage.AtPickupHub), (3, 12, Stage.AtPickupHub),
        (1, 3, Stage.PickedUp), (0, 1, Stage.Pending), (0, 2, Stage.Pending), (2, 4, Stage.Pending),
        (3, 6, Stage.Pending), (1, 5, Stage.Pending)
    ];

    private enum Stage
    {
        Pending,
        PickedUp,
        AtPickupHub,
        InTransit,
        OutForDelivery,
        Delivered,
        PartlyDelivered,
        OnHold,
        Returning,
        Returned
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (await db.Parcels.AnyAsync(cancellationToken))
        {
            return;
        }

        var tenant = tenantContext.Require();
        var now = time.GetUtcNow().UtcDateTime;
        var today = tenant.Today(now);
        var merchants = await db.Merchants
            .Where(m => m.Status == MerchantStatus.Active)
            .OrderBy(m => m.Id)
            .ToListAsync(cancellationToken);
        var points = await db.PickupPoints.Where(p => p.IsDefault && !p.Archived).ToListAsync(cancellationToken);
        var areas = await db.Areas.Include(a => a.Zone).ToDictionaryAsync(a => a.Name, cancellationToken);
        var areaZones = areas.Values.ToDictionary(a => a.Id, a => a.Zone!);
        var rates = await db.DeliveryRates.ToDictionaryAsync(r => r.ServiceArea, cancellationToken);
        var riders = await db.Riders.Where(r => !r.Archived).OrderBy(r => r.Id).ToListAsync(cancellationToken);
        if (merchants.Count < 4 || rates.Count < 3)
        {
            logger.LogWarning("Demo parcels skipped: the demo merchants or the rate card are missing");
            return;
        }

        // 1. Book every parcel
        var parcels = new List<(Parcel Parcel, Stage Stage)>();
        foreach (var (merchantIndex, recipientIndex, stage) in Plan)
        {
            var merchant = merchants[merchantIndex];
            var point = points.Single(p => p.MerchantId == merchant.Id);
            var pickupZone = areaZones[point.AreaId];
            var recipient = Recipients[recipientIndex];
            var area = areas[recipient.Area];
            var charges = rates[ServiceAreas.Between(pickupZone, area.Zone!)].ChargesFor(recipient.WeightGrams);
            var parcel = Parcel.Create(new NewParcel(
                merchant.Id,
                point.Id,
                pickupZone.HubId,
                new ParcelDetails(
                    area.Id,
                    area.Zone!.HubId,
                    recipient.Name,
                    PhoneNumber.Parse(recipient.Phone).Value,
                    recipient.Address,
                    recipient.Cod,
                    recipient.WeightGrams,
                    recipient.Item,
                    recipientIndex % 3 == 0 ? "Call before coming" : null,
                    charges))
            {
                MerchantReference = $"INV-{2000 + parcels.Count}"
            }).Value;
            db.Parcels.Add(parcel);
            parcels.Add((parcel, stage));
        }

        await db.SaveChangesAsync(cancellationToken);

        // 2. Collect and move them through the hubs. All but the one just collected were picked up two days ago, so the
        // inside-city parcels still out with a rider are late and show it
        foreach (var (parcel, stage) in parcels.Where(p => p.Stage != Stage.Pending))
        {
            var pickedUpOn = stage == Stage.PickedUp ? today : today.AddDays(-2);
            parcel.PickUp(pickedUpOn);
            if (stage == Stage.PickedUp)
            {
                continue;
            }

            parcel.ReceiveAt(parcel.PickupHubId, pickedUpOn);
            if (stage == Stage.AtPickupHub || parcel.PickupHubId == parcel.DeliveryHubId)
            {
                continue;
            }

            parcel.DispatchTo(parcel.PickupHubId, parcel.DeliveryHubId);
            if (stage != Stage.InTransit)
            {
                parcel.ReceiveAt(parcel.DeliveryHubId, today);
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        // 3. Hand the ones that went further to a rider of their delivering hub
        var toRiders = parcels.Where(p => p.Stage >= Stage.OutForDelivery).ToList();
        var runs = new Dictionary<long, DeliveryRun>();
        foreach (var hubId in toRiders.Select(p => p.Parcel.DeliveryHubId).Distinct())
        {
            var rider = riders.First(r => r.HubId == hubId);
            var run = DeliveryRun.Open(rider, today);
            db.DeliveryRuns.Add(run);
            runs[hubId] = run;
        }

        await db.SaveChangesAsync(cancellationToken);

        var attempts = new Dictionary<Parcel, DeliveryAttempt>();
        foreach (var (parcel, _) in toRiders)
        {
            var run = runs[parcel.DeliveryHubId];
            parcel.AssignTo(run.RiderId, parcel.DeliveryHubId);
            var attempt = run.Add(parcel, now).Value;
            db.DeliveryAttempts.Add(attempt);
            attempts[parcel] = attempt;
        }

        await db.SaveChangesAsync(cancellationToken);

        // 4. What happened at the door
        foreach (var (parcel, stage) in toRiders.Where(p => p.Stage != Stage.OutForDelivery))
        {
            var attempt = attempts[parcel];
            switch (stage)
            {
                case Stage.Delivered:
                    parcel.Deliver(parcel.CodAmount, null, now);
                    attempt.Complete(AttemptOutcome.Delivered, parcel.CodAmount, null, now);
                    break;
                case Stage.PartlyDelivered:
                    var part = Math.Round(parcel.CodAmount / 2);
                    parcel.Deliver(part, "Kept one of the two sarees", now);
                    attempt.Complete(AttemptOutcome.PartlyDelivered, part, "Kept one of the two sarees", now);
                    break;
                case Stage.OnHold:
                    parcel.Hold("Recipient asked for tomorrow", today.AddDays(1), today, tenant.MaxDeliveryAttempts);
                    attempt.Complete(AttemptOutcome.Hold, 0, "Recipient asked for tomorrow", now);
                    break;
                default:
                    parcel.Refuse("Recipient refused: ordered by mistake");
                    attempt.Complete(AttemptOutcome.Refused, 0, "Recipient refused: ordered by mistake", now);
                    break;
            }

            if (parcel.IsFinal)
            {
                db.LedgerEntries.AddRange(LedgerEntry.For(parcel, today));
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        // 5. One refused parcel comes all the way back to its merchant
        foreach (var (parcel, _) in parcels.Where(p => p.Stage == Stage.Returned))
        {
            parcel.ReceiveAt(parcel.DeliveryHubId, today);
            if (parcel.PickupHubId != parcel.DeliveryHubId)
            {
                parcel.DispatchTo(parcel.DeliveryHubId, parcel.PickupHubId);
                parcel.ReceiveAt(parcel.PickupHubId, today);
            }

            parcel.ReturnToMerchant(parcel.PickupHubId, now);
            db.LedgerEntries.AddRange(LedgerEntry.For(parcel, today));
        }

        // 6. Today's pickups: one waiting for a rider, one with a rider on the way
        var first = points.Single(p => p.MerchantId == merchants[0].Id);
        db.PickupRequests.Add(PickupRequest.Schedule(merchants[0].Id, first.Id, areaZones[first.AreaId].HubId, today));
        var second = points.Single(p => p.MerchantId == merchants[1].Id);
        var collect = PickupRequest.Schedule(merchants[1].Id, second.Id, areaZones[second.AreaId].HubId, today);
        var collector = riders.FirstOrDefault(r => r.HubId == collect.HubId);
        if (collector is not null)
        {
            var fleet = new Fleet(await db.VehicleCapacities.ToListAsync(cancellationToken));
            collect.Assign(collector, fleet, Load.Of([]));
        }

        db.PickupRequests.Add(collect);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Demo parcels ready: {Count} parcels in every stage", parcels.Count);
    }

    private sealed record Recipient(string Name, string Phone, string Area, string Address, decimal Cod, int WeightGrams, string Item);
}
