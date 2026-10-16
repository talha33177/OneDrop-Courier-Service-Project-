using Domain.Common;

namespace Domain.Delivery;

/// <summary>Parcels carried at once: how many, what they weigh together and the heaviest one.</summary>
public readonly record struct Load(int Parcels, int Grams, int HeaviestGrams)
{
    /// <summary>The load of parcels weighing <paramref name="grams"/>, one weight each.</summary>
    public static Load Of(IReadOnlyCollection<int> grams)
    {
        return new Load(grams.Count, grams.Sum(), grams.DefaultIfEmpty().Max());
    }

    public Load With(int grams)
    {
        return new Load(Parcels + 1, Grams + grams, Math.Max(HeaviestGrams, grams));
    }

    public Load Plus(Load other)
    {
        return new Load(Parcels + other.Parcels, Grams + other.Grams, Math.Max(HeaviestGrams, other.HeaviestGrams));
    }
}

public sealed record CapacityValues(int MaxParcels, int MaxLoadGrams, int MaxParcelGrams);

/// <summary>
/// What one kind of vehicle carries at once, in the courier's own numbers: how many parcels, how much weight in all and
/// how heavy one parcel may be. A rider is never handed more than their vehicle carries; a pickup goes to a vehicle
/// big enough for it. One row per vehicle a tenant (<c>UX_VehicleCapacity_Tenant_Vehicle</c>).
/// </summary>
public class VehicleCapacity : TenantEntity
{
    public const int MaxParcelsLimit = 5_000;
    public const int MaxLoadGramsLimit = 10_000_000;

    private VehicleCapacity()
    {
    }

    public Vehicle Vehicle { get; private set; }

    public int MaxParcels { get; private set; }

    public int MaxLoadGrams { get; private set; }

    public int MaxParcelGrams { get; private set; }

    public static Result<VehicleCapacity> Create(Vehicle vehicle, CapacityValues values)
    {
        var capacity = new VehicleCapacity { Vehicle = vehicle };
        var changed = capacity.Change(values);

        return changed.IsSuccess ? capacity : changed.Error!;
    }

    public Result Change(CapacityValues values)
    {
        if (values.MaxParcels is < 1 or > MaxParcelsLimit)
        {
            return Error.Validation("capacity.parcels", $"The number of parcels must be between 1 and {MaxParcelsLimit:N0}.");
        }

        if (values.MaxLoadGrams is < 1_000 or > MaxLoadGramsLimit)
        {
            return Error.Validation("capacity.load", $"The load must be between 1 kg and {MaxLoadGramsLimit / 1000:N0} kg.");
        }

        if (values.MaxParcelGrams < 100 || values.MaxParcelGrams > values.MaxLoadGrams)
        {
            return Error.Validation("capacity.parcel", "The heaviest parcel must be at least 0.1 kg and no more than the whole load.");
        }

        MaxParcels = values.MaxParcels;
        MaxLoadGrams = values.MaxLoadGrams;
        MaxParcelGrams = values.MaxParcelGrams;

        return Result.Success();
    }

    public bool Carries(Load load)
    {
        return load.Parcels <= MaxParcels && load.Grams <= MaxLoadGrams && load.HeaviestGrams <= MaxParcelGrams;
    }

    /// <summary>
    /// Whether <paramref name="parcel"/>, weighing <paramref name="grams"/>, can go on top of what the rider already
    /// <paramref name="carries"/>. The error says which limit it breaks.
    /// </summary>
    public Result Take(Load carries, string parcel, int grams)
    {
        var name = Vehicle.DisplayName().ToLowerInvariant();
        if (grams > MaxParcelGrams)
        {
            return Error.Conflict(
                "capacity.parcel",
                $"{parcel} weighs {Weight.Kg(grams)}; a {name} takes parcels up to {Weight.Kg(MaxParcelGrams)}.");
        }

        if (carries.Parcels >= MaxParcels)
        {
            return Error.Conflict("capacity.parcels", $"{parcel} does not fit: the {name} is full with {MaxParcels} parcels.");
        }

        if (carries.Grams + grams > MaxLoadGrams)
        {
            return Error.Conflict(
                "capacity.load",
                $"{parcel} does not fit: it weighs {Weight.Kg(grams)} and the {name} has " +
                $"{Weight.Kg(Math.Max(0, MaxLoadGrams - carries.Grams))} of its {Weight.Kg(MaxLoadGrams)} left.");
        }

        return Result.Success();
    }
}

/// <summary>
/// The courier's vehicles and what each carries. A vehicle with no capacity set has no limit: the courier has not said
/// what it carries.
/// </summary>
public sealed class Fleet(IEnumerable<VehicleCapacity> capacities)
{
    private readonly Dictionary<Vehicle, VehicleCapacity> byVehicle = capacities.ToDictionary(c => c.Vehicle);

    public VehicleCapacity? this[Vehicle vehicle] => byVehicle.GetValueOrDefault(vehicle);

    /// <summary>Whether a rider on <paramref name="vehicle"/> carrying <paramref name="carries"/> can take one more parcel.</summary>
    public Result Take(Vehicle vehicle, Load carries, string parcel, int grams)
    {
        return this[vehicle]?.Take(carries, parcel, grams) ?? Result.Success();
    }

    /// <summary>
    /// How many trips a vehicle needs to carry <paramref name="load"/> once its capacity is known: one when it fits (or
    /// when the courier has set no limit for it).
    /// </summary>
    public int Trips(Vehicle vehicle, Load load)
    {
        if (this[vehicle] is not { } own)
        {
            return 1;
        }

        return Math.Max(1, Math.Max((load.Parcels + own.MaxParcels - 1) / own.MaxParcels, (load.Grams + own.MaxLoadGrams - 1) / own.MaxLoadGrams));
    }

    /// <summary>
    /// Whether a rider on <paramref name="vehicle"/> already <paramref name="carries"/> this much (parcels with them, and
    /// parcels they are on their way to collect) can also be sent to collect <paramref name="load"/>. Only a pickup van
    /// collects. It must carry everything together, except that a pickup bigger than the whole van takes the van when
    /// it is otherwise empty: nothing the courier has carries it in one trip, so the van makes more than one.
    /// </summary>
    public Result Collect(Vehicle vehicle, Load carries, Load load)
    {
        var name = vehicle.DisplayName().ToLowerInvariant();
        if (!vehicle.CollectsPickups())
        {
            return Error.Conflict(
                "pickup.vehicle",
                $"A {name} does not collect from merchants: it takes parcels to recipients. Send a rider with a pickup van.");
        }

        var own = this[vehicle];
        var together = carries.Plus(load);
        if (own is null || own.Carries(together) || (carries.Parcels == 0 && !own.Carries(load)))
        {
            return Result.Success();
        }

        var why = together.Parcels > own.MaxParcels
            ? $"With this pickup the {name} would have {together.Parcels} parcels; it takes {own.MaxParcels} at once."
            : together.Grams > own.MaxLoadGrams
                ? $"With this pickup the {name} would have {Weight.Kg(together.Grams)}; it takes {Weight.Kg(own.MaxLoadGrams)} at once."
                : $"One parcel booked weighs {Weight.Kg(load.HeaviestGrams)}; a {name} takes parcels up to {Weight.Kg(own.MaxParcelGrams)}.";

        return Error.Conflict(
            "pickup.full",
            $"{why} Give it to another pickup van, or after this one has unloaded at the hub.");
    }
}
