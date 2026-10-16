using Application.Abstractions;

namespace Application.Common;

/// <summary>Days and times in the tenant's own time zone: runs, pickups and payouts follow the courier's calendar.</summary>
public static class TenantTime
{
    public static TimeZoneInfo Zone(this TenantInfo tenant)
    {
        return TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZone);
    }

    public static DateOnly Today(this TenantInfo tenant, DateTime utcNow)
    {
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, tenant.Zone()));
    }

    /// <summary>A stored UTC time as the tenant's wall clock shows it.</summary>
    public static DateTime Local(this TenantInfo tenant, DateTime utc)
    {
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tenant.Zone());
    }

    /// <summary>The first moment of <paramref name="day"/> in the tenant's zone, as UTC: the lower bound of a day's rows.</summary>
    public static DateTime StartUtc(this TenantInfo tenant, DateOnly day)
    {
        return TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), tenant.Zone());
    }

    /// <summary>
    /// When riders are due back at their hub from a run on <paramref name="runDate"/>, on the tenant's clock; null when
    /// the tenant sets no time.
    /// </summary>
    public static DateTime? DueBack(this TenantInfo tenant, DateOnly runDate)
    {
        return tenant.RiderReturnTime is { } time ? runDate.ToDateTime(time) : null;
    }

    /// <summary>A run of <paramref name="runDate"/> still open after its riders were due back.</summary>
    public static bool RunIsLate(this TenantInfo tenant, DateOnly runDate, DateTime utcNow)
    {
        return tenant.DueBack(runDate) is { } due && tenant.Local(utcNow) > due;
    }

    /// <summary>The tenant the request or job runs for; a use case that needs one cannot run without it.</summary>
    public static TenantInfo Require(this ITenantContext context)
    {
        return context.Tenant ?? throw new InvalidOperationException("This needs a tenant.");
    }
}

/// <summary>One page of a list, with the total so the page can show how many there are in all.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, int Total, int Number, int Size)
{
    public int Pages => Math.Max(1, (Total + Size - 1) / Size);

    public bool HasPrevious => Number > 1;

    public bool HasNext => Number < Pages;
}
