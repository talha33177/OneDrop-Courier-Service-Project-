using System.Security.Cryptography;
using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Application.Delivery.Pickups;
using Domain.Common;
using Domain.Parcels;
using Domain.Pricing;

namespace Application.Parcels.CreateParcel;

/// <summary>
/// Books a parcel for the calling merchant: validate, resolve the route and the charges, save it with its first
/// tracking line, and put its pickup point on the pickup list of the day it goes with. A retry with the same
/// Idempotency-Key answers with the first parcel; the same key with another body is refused. Only an active merchant
/// books.
/// </summary>
public class CreateParcelHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    ITenantContext tenantContext,
    ParcelBooking booking,
    PickupSchedule pickups,
    IValidator<CreateParcelCommand> validator)
{
    public static readonly Error MerchantRequired =
        Error.Forbidden("parcel.merchantRequired", "Parcels are booked by a merchant of this courier.");

    public static readonly Error NotActive = Error.Forbidden(
        "parcel.merchantNotActive",
        "Your account is not active yet. Parcels can be booked once the courier has approved it.");

    private static readonly JsonSerializerOptions HashOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<CreateParcelResult>> HandleAsync(
        CreateParcelCommand command,
        CancellationToken cancellationToken = default)
    {
        var merchantId = currentUser.MerchantId;
        if (merchantId is null)
        {
            return MerchantRequired;
        }

        var requestHash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command, HashOptions));
        var replay = await FindReplayAsync(merchantId.Value, command.IdempotencyKey, requestHash, cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        var prepared = await PrepareAsync(command, requestHash, cancellationToken);
        if (prepared.IsFailure)
        {
            return prepared.Error!;
        }

        var (parcel, area, hub) = prepared.Value;
        db.Parcels.Add(parcel);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (command.IdempotencyKey is not null)
        {
            // The same key raced us to the unique index: answer with the parcel that won
            db.Entry(parcel).State = EntityState.Detached;
            var winner = await FindReplayAsync(merchantId.Value, command.IdempotencyKey, requestHash, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return winner;
        }

        await pickups.ScheduleAsync([parcel], cancellationToken);

        return Describe(parcel, area, hub, tenantContext.Require());
    }

    /// <summary>
    /// A parcel ready to save, with its area and delivering hub's names, or why it cannot be booked. A bulk upload checks
    /// every row this way before saving any.
    /// </summary>
    public async Task<Result<(Parcel Parcel, string Area, string Hub)>> PrepareAsync(
        CreateParcelCommand command,
        byte[]? requestHash,
        CancellationToken cancellationToken = default)
    {
        var merchantId = currentUser.MerchantId ?? throw new InvalidOperationException("A merchant books parcels.");
        var canBook = await db.Merchants
            .Where(m => m.Id == merchantId)
            .Select(m => m.Status == Domain.Merchants.MerchantStatus.Active && !m.Archived)
            .SingleAsync(cancellationToken);
        if (!canBook)
        {
            return NotActive;
        }

        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            return validation.ToError();
        }

        var route = await booking.ResolveAsync(
            merchantId,
            command.PickupPointId,
            command.AreaId,
            command.Area,
            command.WeightGrams,
            command.Speed ?? DeliverySpeed.Regular,
            command.ProductType ?? ProductType.General,
            cancellationToken);
        if (route.IsFailure)
        {
            return route.Error!;
        }

        var resolved = route.Value;
        var created = Parcel.Create(new NewParcel(
            merchantId,
            resolved.PickupPoint.Id,
            resolved.PickupHubId,
            new ParcelDetails(
                resolved.Area.Id,
                resolved.DeliveryHub.Id,
                command.RecipientName!,
                PhoneNumber.Parse(command.RecipientPhone).Value,
                command.RecipientAddress!,
                command.CodAmount,
                command.WeightGrams,
                command.ItemDescription,
                command.Note,
                resolved.Charges)
            {
                Pin = GeoPoint.From(command.RecipientLatitude, command.RecipientLongitude).Value
            })
        {
            MerchantReference = command.MerchantReference,
            IdempotencyKey = command.IdempotencyKey,
            RequestHash = requestHash
        });

        return created.IsSuccess
            ? (created.Value, resolved.Area.Name, resolved.DeliveryHub.Name)
            : created.Error!;
    }

    public static CreateParcelResult Describe(Parcel parcel, string area, string hub, TenantInfo tenant)
    {
        return new CreateParcelResult(
            parcel.TrackingCode,
            parcel.MerchantReference,
            parcel.Status,
            area,
            hub,
            parcel.ServiceArea,
            parcel.CodAmount,
            parcel.DeliveryCharge,
            parcel.Charges.CodChargeOn(parcel.CodAmount),
            parcel.TotalCharge,
            parcel.Created,
            parcel.Speed,
            parcel.ProductType,
            parcel.SpeedCharge,
            parcel.ProductCharge,
            parcel.Status == ParcelStatus.Pending ? tenant.PickupDayFor(parcel.Created) : null);
    }

    private async Task<Result<CreateParcelResult>?> FindReplayAsync(
        long merchantId,
        string? idempotencyKey,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return null;
        }

        var previous = await (
            from parcel in db.Parcels
            join area in db.Areas on parcel.AreaId equals area.Id
            join hub in db.Hubs on parcel.DeliveryHubId equals hub.Id
            where parcel.MerchantId == merchantId && parcel.IdempotencyKey == idempotencyKey
            select new { Parcel = parcel, Area = area.Name, Hub = hub.Name })
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        if (previous is null)
        {
            return null;
        }

        if (previous.Parcel.RequestHash is null || !previous.Parcel.RequestHash.AsSpan().SequenceEqual(requestHash))
        {
            return Error.Conflict(
                "parcel.idempotencyKey.reused",
                "This Idempotency-Key was already used for a different parcel.");
        }

        return Describe(previous.Parcel, previous.Area, previous.Hub, tenantContext.Require()) with { Replayed = true };
    }
}
