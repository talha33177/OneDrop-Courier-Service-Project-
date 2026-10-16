using Domain.Network;

namespace Domain.Tests;

/// <summary>The coverage map the courier keeps itself: hubs, the zones they serve and the areas addresses pick from.</summary>
public class CoverageTests
{
    [Fact]
    public void A_hub_holds_its_code_in_capitals_and_trims_what_it_is_given()
    {
        var hub = Hub.Create(" mir ", "  Mirpur hub ", " Road 3, Mirpur ", " 01700-100101 ");

        Assert.True(hub.IsSuccess);
        Assert.Equal("MIR", hub.Value.Code);
        Assert.Equal("Mirpur hub", hub.Value.Name);
        Assert.Equal("Road 3, Mirpur", hub.Value.Address);
        Assert.Equal("01700-100101", hub.Value.Phone);
        Assert.False(hub.Value.Archived);
    }

    [Theory]
    [InlineData("", "hub.code")]
    [InlineData("MIR 10", "hub.code")]
    [InlineData("MIR-10", "hub.code")]
    public void A_hub_code_is_letters_and_digits_and_is_required(string code, string error)
    {
        var hub = Hub.Create(code, "Mirpur hub", "Road 3", "01700-100101");

        Assert.True(hub.IsFailure);
        Assert.Equal(error, hub.Error!.Code);
    }

    [Fact]
    public void A_hub_needs_a_name_an_address_and_a_phone_number()
    {
        Assert.Equal("hub.name", Hub.Create("MIR", " ", "Road 3", "01700-100101").Error!.Code);
        Assert.Equal("hub.address", Hub.Create("MIR", "Mirpur hub", "", "01700-100101").Error!.Code);
        Assert.Equal("hub.phone", Hub.Create("MIR", "Mirpur hub", "Road 3", null).Error!.Code);
        Assert.Equal("hub.name", Hub.Create("MIR", new string('a', 201), "Road 3", "01700").Error!.Code);
    }

    [Fact]
    public void A_hub_closes_and_opens_again_without_losing_what_it_is()
    {
        var hub = Hub.Create("MIR", "Mirpur hub", "Road 3", "01700-100101").Value;

        hub.Archive();
        Assert.True(hub.Archived);

        hub.Restore();
        Assert.False(hub.Archived);
        Assert.Equal("Mirpur hub", hub.Name);
    }

    [Fact]
    public void A_zone_carries_the_city_and_suburb_flag_that_price_a_parcel()
    {
        var zone = Zone.Create("sav", "Savar", 3, " Dhaka ", isSuburb: true);

        Assert.True(zone.IsSuccess);
        Assert.Equal("SAV", zone.Value.Code);
        Assert.Equal("Dhaka", zone.Value.City);
        Assert.True(zone.Value.IsSuburb);
        Assert.Equal(3, zone.Value.HubId);
    }

    [Fact]
    public void A_zone_is_filed_under_one_of_the_eight_divisions_or_none()
    {
        Assert.Equal("Sylhet", Zone.Create("MOU", "Moulvibazar", 3, "Moulvibazar", isSuburb: false, " sylhet ").Value.Division);
        Assert.Null(Zone.Create("MOU", "Moulvibazar", 3, "Moulvibazar", isSuburb: false).Value.Division);
        Assert.Equal("zone.division", Zone.Create("MOU", "Moulvibazar", 3, "Moulvibazar", isSuburb: false, "Atlantis").Error!.Code);
        Assert.Equal(8, Divisions.All.Count);
    }

    [Fact]
    public void A_zone_needs_a_code_a_name_and_a_city()
    {
        Assert.Equal("zone.code", Zone.Create(" ", "Savar", 3, "Dhaka", isSuburb: true).Error!.Code);
        Assert.Equal("zone.name", Zone.Create("SAV", null, 3, "Dhaka", isSuburb: true).Error!.Code);
        Assert.Equal("zone.city", Zone.Create("SAV", "Savar", 3, "   ", isSuburb: true).Error!.Code);
        Assert.Equal("zone.city", Zone.Create("SAV", "Savar", 3, new string('a', 61), isSuburb: true).Error!.Code);
    }

    [Fact]
    public void A_zone_moves_to_another_hub_and_changes_its_flag_when_it_is_changed()
    {
        var zone = Zone.Create("SAV", "Savar", 3, "Dhaka", isSuburb: true).Value;

        var changed = zone.Change("SAV", "Savar", 7, "Dhaka", isSuburb: false);

        Assert.True(changed.IsSuccess);
        Assert.Equal(7, zone.HubId);
        Assert.False(zone.IsSuburb);
    }

    [Fact]
    public void An_area_needs_a_name_and_keeps_the_zone_it_is_given()
    {
        var area = Area.Create(" Mirpur 10 ", 4);

        Assert.True(area.IsSuccess);
        Assert.Equal("Mirpur 10", area.Value.Name);
        Assert.Equal(4, area.Value.ZoneId);
        Assert.Equal("area.name", Area.Create("  ", 4).Error!.Code);
        Assert.Equal("area.name", Area.Create(new string('a', 201), 4).Error!.Code);
    }

    [Fact]
    public void An_area_leaves_the_map_and_comes_back_keeping_its_zone()
    {
        var area = Area.Create("Mirpur 10", 4).Value;

        area.Archive();
        Assert.True(area.Archived);

        area.Restore();
        Assert.False(area.Archived);
        Assert.Equal(4, area.ZoneId);
    }
}
