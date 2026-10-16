using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Application.Common;
using Application.Parcels.Browse;
using Application.Parcels.CreateParcel;
using Application.Platform.Staff;
using Domain.Common;
using Domain.Parcels;
using Domain.Pricing;

namespace Application.Parcels.ParcelActions;

/// <summary>
/// What a merchant does with a parcel after booking it: correct it or cancel it before pickup, or ask for it back later.
/// Courier staff can also ask for a parcel back, weigh it, and flag a problem on it. A merchant finds only its own
/// parcels.
/// </summary>
public class ParcelActionsHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    StaffAccess staff,
    ParcelBooking booking,
    IValidator<CreateParcelCommand> validator,
    TimeProvider time)
{
    /// <summary>
    /// New details for a parcel still waiting for pickup, priced again at today's rates; the pickup point stays. The
    /// tracking code, reference and idempotency key of <paramref name="details"/> are ignored.
    /// </summary>
    public async Task<Result> EditAsync(string trackingCode, CreateParcelCommand details, CancellationToken cancellationToken = default)
    {
        var merchantId = currentUser.MerchantId ?? throw new InvalidOperationException("A merchant edits its parcels.");
        var parcel = await FindAsync(trackingCode, cancellationToken);
        if (parcel is null)
        {
            return ParcelDetailsHandler.NotFound;
        }

        var validation = await validator.ValidateAsync(details, cancellationToken);
        if (!validation.IsValid)
        {
            return validation.ToError();
        }

        var route = await booking.ResolveAsync(
            merchantId,
            parcel.PickupPointId,
            details.AreaId,
            details.Area,
            details.WeightGrams,
            details.Speed,
            details.ProductType,
            cancellationToken);
        if (route.IsFailure)
        {
            return route.Error!;
        }

        var edited = parcel.Edit(new ParcelDetails(
            route.Value.Area.Id,
            route.Value.DeliveryHub.Id,
            details.RecipientName!,
            PhoneNumber.Parse(details.RecipientPhone).Value,
            details.RecipientAddress!,
            details.CodAmount,
            details.WeightGrams,
            details.ItemDescription,
            details.Note,
            route.Value.Charges)
        {
            Pin = GeoPoint.From(details.RecipientLatitude, details.RecipientLongitude).Value
        });

        return await SaveAsync(edited, cancellationToken);
    }

    public async Task<Result> CancelAsync(string trackingCode, string? reason, CancellationToken cancellationToken = default)
    {
        var parcel = await FindAsync(trackingCode, cancellationToken);
        if (parcel is null)
        {
            return ParcelDetailsHandler.NotFound;
        }

        if (reason?.Trim().Length > 200)
        {
            return Error.Validation("parcel.cancel.reason", "Say why in at most 200 characters.");
        }

        return await SaveAsync(parcel.Cancel(reason, time.GetUtcNow().UtcDateTime), cancellationToken);
    }

    /// <summary>
    /// The weight a hub's scale read. The parcel is charged on it at the rates it was booked under, so a rate card
    /// changed since booking never reaches it. Courier staff only: the pages that call this are the hub's.
    /// </summary>
    public async Task<Result> ReweighAsync(string trackingCode, int measuredGrams, CancellationToken cancellationToken = default)
    {
        var parcel = await FindAtYourHubAsync(trackingCode, cancellationToken);
        if (parcel.IsFailure)
        {
            return parcel.Error!;
        }

        return await SaveAsync(parcel.Value.Reweigh(measuredGrams, null), cancellationToken);
    }

    /// <summary>
    /// Courier staff flag a problem for the courier to look at; the parcel does not go out to the door until it is
    /// cleared. The pages that call this are the hub's.
    /// </summary>
    public async Task<Result> FlagAsync(string trackingCode, ParcelIssue issue, string? note, CancellationToken cancellationToken = default)
    {
        var parcel = await FindAtYourHubAsync(trackingCode, cancellationToken);
        if (parcel.IsFailure)
        {
            return parcel.Error!;
        }

        return await SaveAsync(parcel.Value.Flag(issue, note, null, time.GetUtcNow().UtcDateTime), cancellationToken);
    }

    /// <summary>Courier staff clear a flagged problem once it has been looked at, saying what was done.</summary>
    public async Task<Result> ClearFlagAsync(string trackingCode, string? note, CancellationToken cancellationToken = default)
    {
        var parcel = await FindAtYourHubAsync(trackingCode, cancellationToken);
        if (parcel.IsFailure)
        {
            return parcel.Error!;
        }

        return await SaveAsync(parcel.Value.ClearFlag(note, null), cancellationToken);
    }

    public async Task<Result> RequestReturnAsync(string trackingCode, string? reason, CancellationToken cancellationToken = default)
    {
        var parcel = await FindAtYourHubAsync(trackingCode, cancellationToken);
        if (parcel.IsFailure)
        {
            return parcel.Error!;
        }

        return await SaveAsync(parcel.Value.RequestReturn(reason), cancellationToken);
    }

    /// <summary>
    /// The parcel for one of the hub's buttons: not found as for anyone else, and refused to a hub's own staff unless it
    /// is at their hub. Head office acts on a parcel wherever it is.
    /// </summary>
    private async Task<Result<Parcel>> FindAtYourHubAsync(string trackingCode, CancellationToken cancellationToken)
    {
        var parcel = await FindAsync(trackingCode, cancellationToken);
        if (parcel is null)
        {
            return ParcelDetailsHandler.NotFound;
        }

        var rights = await staff.CurrentAsync(cancellationToken);

        return rights.WorksAt(parcel.CurrentHubId) ? parcel : StaffRights.NotAtYourHub;
    }

    private Task<Parcel?> FindAsync(string trackingCode, CancellationToken cancellationToken)
    {
        var code = trackingCode.Trim().ToUpperInvariant();

        return db.Parcels.SingleOrDefaultAsync(p => p.TrackingCode == code, cancellationToken);
    }

    private async Task<Result> SaveAsync(Result changed, CancellationToken cancellationToken)
    {
        if (changed.IsFailure)
        {
            return changed;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("parcel.changed", "The parcel was updated by someone else just now. Open it again.");
        }

        return Result.Success();
    }
}
