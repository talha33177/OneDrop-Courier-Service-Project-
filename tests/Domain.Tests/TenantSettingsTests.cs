using Domain.Platform;

namespace Domain.Tests;

/// <summary>The settings a courier's admin keeps: its name, hotline, SMS sender, attempts, businesses and the day's times.</summary>
public class TenantSettingsTests
{
    private static readonly TenantSettings Valid = new(
        " OneDrop Courier ", " 09610-001122 ", " OneDrop ", 3, 10, new TimeOnly(20, 0), new TimeOnly(15, 30), 3);

    // Couriers come from the database scripts, so nothing in the domain makes one
    private static Tenant NewTenant()
    {
        return (Tenant)Activator.CreateInstance(typeof(Tenant), nonPublic: true)!;
    }

    [Fact]
    public void A_courier_keeps_its_settings_trimmed()
    {
        var tenant = NewTenant();

        Assert.True(tenant.ChangeSettings(Valid).IsSuccess);
        Assert.Equal("OneDrop Courier", tenant.Name);
        Assert.Equal("09610-001122", tenant.SupportPhone);
        Assert.Equal("OneDrop", tenant.SmsSenderName);
        Assert.Equal(3, tenant.MaxDeliveryAttempts);
        Assert.Equal(10, tenant.MaxBusinessesPerAccount);
        Assert.Equal(new TimeOnly(20, 0), tenant.RiderReturnTime);
        Assert.Equal(new TimeOnly(15, 30), tenant.PickupTime);
        Assert.Equal(3, tenant.StuckAfterDays);
    }

    [Fact]
    public void The_times_and_the_business_limit_may_be_left_empty()
    {
        var tenant = NewTenant();

        Assert.True(tenant.ChangeSettings(Valid with { MaxBusinessesPerAccount = null, RiderReturnTime = null, PickupTime = null, StuckAfterDays = null }).IsSuccess);
        Assert.Null(tenant.MaxBusinessesPerAccount);
        Assert.Null(tenant.PickupTime);
    }

    [Fact]
    public void Each_setting_is_checked_and_nothing_changes_on_a_refusal()
    {
        var tenant = NewTenant();
        tenant.ChangeSettings(Valid);

        Assert.Equal("tenant.name", tenant.ChangeSettings(Valid with { Name = " " }).Error!.Code);
        Assert.Equal("tenant.supportPhone", tenant.ChangeSettings(Valid with { SupportPhone = "call us" }).Error!.Code);
        Assert.Equal("tenant.supportPhone", tenant.ChangeSettings(Valid with { SupportPhone = "" }).Error!.Code);
        Assert.Equal("tenant.smsSender", tenant.ChangeSettings(Valid with { SmsSenderName = "OneDropCourier" }).Error!.Code);
        Assert.Equal("tenant.smsSender", tenant.ChangeSettings(Valid with { SmsSenderName = "One Drop" }).Error!.Code);
        Assert.Equal("tenant.attempts", tenant.ChangeSettings(Valid with { MaxDeliveryAttempts = 0 }).Error!.Code);
        Assert.Equal("tenant.attempts", tenant.ChangeSettings(Valid with { MaxDeliveryAttempts = Tenant.MaxAttempts + 1 }).Error!.Code);
        Assert.Equal("tenant.businesses", tenant.ChangeSettings(Valid with { MaxBusinessesPerAccount = 0 }).Error!.Code);
        Assert.Equal("tenant.stuck", tenant.ChangeSettings(Valid with { StuckAfterDays = 0 }).Error!.Code);
        Assert.Equal("tenant.stuck", tenant.ChangeSettings(Valid with { StuckAfterDays = Tenant.MaxStuckAfterDays + 1 }).Error!.Code);

        var refused = tenant.ChangeSettings(Valid with { Name = "Someone else", MaxDeliveryAttempts = 0 });
        Assert.True(refused.IsFailure);
        Assert.Equal("OneDrop Courier", tenant.Name);
        Assert.Equal(3, tenant.MaxDeliveryAttempts);
    }
}
