using Microsoft.EntityFrameworkCore;
using Application.Abstractions;
using Domain.Common;
using Domain.Pricing;

namespace Application.Pricing.Rates;

public sealed record RateRow(
    ServiceArea ServiceArea,
    int IncludedWeightGrams,
    decimal BaseCharge,
    decimal ExtraKgCharge,
    decimal CodChargePercent,
    decimal ReturnCharge,
    int? DeliveryDays);

/// <summary>
/// The courier's rate card and delivery times: shown on the public pricing section and the merchant's
/// calculator, changed by the courier's admin. A change prices, and promises a time for, parcels booked from then on.
/// </summary>
public class RatesHandler(IAppDbContext db)
{
    public async Task<IReadOnlyList<RateRow>> ListAsync(CancellationToken cancellationToken = default)
    {
        return await db.DeliveryRates
            .OrderBy(r => r.ServiceArea)
            .Select(r => new RateRow(r.ServiceArea, r.IncludedWeightGrams, r.BaseCharge, r.ExtraKgCharge, r.CodChargePercent, r.ReturnCharge, r.DeliveryDays))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<Result> ChangeAsync(ServiceArea area, RateValues values, CancellationToken cancellationToken = default)
    {
        var rate = await db.DeliveryRates.SingleOrDefaultAsync(r => r.ServiceArea == area, cancellationToken);
        if (rate is null)
        {
            var created = DeliveryRate.Create(area, values);
            if (created.IsFailure)
            {
                return created.Error!;
            }

            db.DeliveryRates.Add(created.Value);
            await db.SaveChangesAsync(cancellationToken);

            return Result.Success();
        }

        var changed = rate.Change(values);
        if (changed.IsSuccess)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }
}
