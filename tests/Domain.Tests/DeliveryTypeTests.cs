using Domain.Common;
using Domain.Parcels;
using Domain.Pricing;

namespace Domain.Tests;

/// <summary>
/// Delivery types: the speeds and kinds of contents the courier's admin makes, their extras per service area, and the
/// door pinned on a map.
/// </summary>
public class DeliveryTypeTests
{
    [Fact]
    public void A_speed_and_what_is_inside_add_their_extras_to_the_delivery_charge()
    {
        var charges = Build.Rate(ServiceArea.Suburb).Price(800, Build.Express(), Build.Fragile()).Value;

        Assert.Equal(160, charges.DeliveryCharge);
        Assert.Equal(40, charges.SpeedCharge);
        Assert.Equal(20, charges.ProductCharge);
        Assert.Equal(1, charges.DeliveryDays);
        Assert.Equal(12, charges.SpeedId);
        Assert.Equal(23, charges.ProductTypeId);
    }

    [Fact]
    public void The_defaults_go_everywhere_at_no_extra_in_the_rate_cards_time()
    {
        var rate = Build.Rate(ServiceArea.OutsideCity);
        var charges = rate.Price(500, Build.Regular(), Build.General()).Value;

        Assert.Equal(120, charges.DeliveryCharge);
        Assert.Equal(0, charges.SpeedCharge + charges.ProductCharge);
        Assert.Equal(3, charges.DeliveryDays);
        decimal Charge(DeliveryOption contents) =>
            Build.Rate(ServiceArea.InsideCity).Price(500, Build.Regular(), contents).Value.DeliveryCharge;
        Assert.True(Charge(Build.Frozen()) > Charge(Build.Fragile()));
        Assert.True(Charge(Build.Fragile()) > Charge(Build.General()));
    }

    [Fact]
    public void What_a_service_area_does_not_offer_is_refused_with_the_reason()
    {
        var outside = Build.Rate(ServiceArea.OutsideCity);

        var express = outside.Price(500, Build.Express(), Build.General()).Error!;
        Assert.Equal("parcel.speed", express.Code);
        Assert.Equal("Express delivery is not offered outside the city. Choose another speed.", express.Message);
        var frozen = outside.Price(500, Build.Regular(), Build.Frozen()).Error!;
        Assert.Equal("parcel.productType", frozen.Code);
        Assert.Equal("The courier does not carry frozen parcels outside the city.", frozen.Message);
        Assert.Equal(140 + 20, outside.Price(1500, Build.Regular(), Build.Fragile()).Value.DeliveryCharge);
    }

    [Fact]
    public void An_option_taken_off_or_left_without_a_price_is_not_offered_and_parcels_booked_keep_theirs()
    {
        var rate = Build.Rate(ServiceArea.InsideCity);
        var express = Build.Express();
        var frozen = Build.Frozen();
        var booked = rate.Price(500, express, frozen).Value;

        Assert.True(express.Archive().IsSuccess);
        Assert.True(frozen.SetCharge(ServiceArea.InsideCity, null, null).IsSuccess);

        Assert.Equal("parcel.speed", rate.Price(500, express, Build.General()).Error?.Code);
        Assert.Equal("parcel.productType", rate.Price(500, Build.Regular(), frozen).Error?.Code);
        Assert.Equal(150, booked.DeliveryCharge);
        express.Restore();
        Assert.True(rate.Price(500, express, Build.General()).IsSuccess);
    }

    [Fact]
    public void A_speed_can_promise_its_own_time_and_a_new_option_is_priced_like_the_rest()
    {
        var sameDay = Build.Option(13, DeliveryOptionKind.Speed, "same-day", "Same day", goesFirst: true, near: 120, days: 0);
        var electronics = Build.Option(26, DeliveryOptionKind.Contents, "electronics", "Electronics", near: 25, far: 35);

        var charges = Build.Rate(ServiceArea.InsideCity).Price(500, sameDay, electronics).Value;

        Assert.Equal(60 + 120 + 25, charges.DeliveryCharge);
        Assert.Equal(0, charges.DeliveryDays);
        Assert.Equal(35, Build.Rate(ServiceArea.OutsideCity).Price(500, Build.Regular(), electronics).Value.ProductCharge);
    }

    [Theory]
    [InlineData(-1, null, "option.charge")]
    [InlineData(10_001, null, "option.charge")]
    [InlineData(null, 1, "option.days")]
    [InlineData(40, -1, "option.days")]
    [InlineData(40, 31, "option.days")]
    public void A_speeds_price_outside_its_limits_is_refused_and_nothing_changes(int? charge, int? days, string code)
    {
        var express = Build.Express();

        Assert.Equal(code, express.SetCharge(ServiceArea.InsideCity, charge, days).Error?.Code);
        Assert.Equal(40, express.ChargeIn(ServiceArea.InsideCity));
        Assert.Equal(1, express.DaysIn(ServiceArea.InsideCity));
    }

    [Fact]
    public void Only_a_speed_promises_a_time_and_the_default_stays_free_and_on_sale()
    {
        Assert.Equal("option.days", Build.Fragile().SetCharge(ServiceArea.InsideCity, 20, 1).Error?.Code);

        var regular = Build.Regular();
        Assert.Equal("option.default", regular.SetCharge(ServiceArea.InsideCity, 10, null).Error?.Code);
        Assert.Equal("option.default", regular.Archive().Error?.Code);
        Assert.Equal(0, regular.ChargeIn(ServiceArea.OutsideCity));
        Assert.False(regular.Archived);
    }

    [Fact]
    public void A_new_default_goes_everywhere_at_no_extra()
    {
        var express = Build.Express();

        Assert.True(express.MakeDefault().IsSuccess);

        Assert.Equal(0, express.ChargeIn(ServiceArea.OutsideCity));
        Assert.Null(express.DaysIn(ServiceArea.InsideCity));
        Assert.Empty(express.Charges);
    }

    [Theory]
    [InlineData("", "package", "option.name")]
    [InlineData("A name far longer than forty characters in all", "package", "option.name")]
    [InlineData("Fragile", "", "option.icon")]
    [InlineData("Fragile", "<svg>", "option.icon")]
    public void An_option_needs_a_short_name_and_an_icon(string name, string icon, string code)
    {
        var fragile = Build.Fragile();

        Assert.Equal(code, fragile.Change(new DeliveryOptionValues(name, null, null, icon, false)).Error?.Code);
        Assert.Equal("Fragile", fragile.Name);
    }

    [Fact]
    public void Only_contents_carry_a_handling_note()
    {
        var express = Build.Express();
        var fragile = Build.Fragile();

        express.Change(new DeliveryOptionValues("Express", null, "Rush it", "zap", true));
        fragile.Change(new DeliveryOptionValues("Fragile", "Glass", "Fragile: handle with care", "glass", false));

        Assert.Null(express.Handling);
        Assert.Equal("Fragile: handle with care", fragile.Handling);
        Assert.Equal("fragile", fragile.Code);
    }

    [Theory]
    [InlineData("Fresh food", "fresh-food")]
    [InlineData("  Same-day!! ", "same-day")]
    [InlineData("Glass & ceramics", "glass-ceramics")]
    [InlineData("৳ only", "only")]
    public void A_code_is_made_from_the_name(string name, string code)
    {
        Assert.Equal(code, DeliveryOption.MakeCode(name));
    }

    [Theory]
    [InlineData("fresh-food", true)]
    [InlineData("Fresh food", true)]
    [InlineData("fresh_food", true)]
    [InlineData("freshFood", true)]
    [InlineData("FRESHFOOD", true)]
    [InlineData("frozen", false)]
    [InlineData("", false)]
    public void An_option_is_named_by_its_code_or_name_in_any_spelling(string text, bool named)
    {
        var fresh = Build.Option(24, DeliveryOptionKind.Contents, "fresh-food", "Fresh food", goesFirst: true, near: 30);

        Assert.Equal(named, fresh.IsNamedBy(text));
    }

    [Fact]
    public void A_parcel_keeps_its_speed_contents_and_extras_and_a_reweigh_keeps_the_extras()
    {
        var parcel = Parcel.Create(new NewParcel(
            7,
            1,
            Build.Mirpur,
            new ParcelDetails(
                1,
                Build.Mirpur,
                "Rahim Uddin",
                PhoneNumber.Parse("01811000101").Value,
                "House 22, Road 4",
                900,
                500,
                "Ice cream",
                null,
                Build.Rate(ServiceArea.InsideCity).Price(500, Build.Express(), Build.Frozen()).Value)
            {
                Pin = GeoPoint.Find("23.8103, 90.4125")
            })).Value;

        Assert.Equal(150, parcel.DeliveryCharge);
        Assert.Equal(12, parcel.SpeedId);
        Assert.Equal(25, parcel.ProductTypeId);
        Assert.Equal(23.8103m, parcel.RecipientLatitude);
        Assert.Equal(90.4125m, parcel.Pin?.Longitude);

        parcel.PickUp(Build.Today);
        Assert.Equal(Build.Today.AddDays(1), parcel.DueOn);
        Assert.True(parcel.Reweigh(2_500, Build.Mirpur).IsSuccess);
        Assert.Equal(60 + 2 * 15 + 40 + 50, parcel.DeliveryCharge);
        Assert.Equal(40, parcel.Charges.SpeedCharge);
        Assert.Equal(12, parcel.Charges.SpeedId);
    }

    [Theory]
    [InlineData("23.8103, 90.4125", 23.8103, 90.4125)]
    [InlineData("23.8103 90.4125", 23.8103, 90.4125)]
    [InlineData("https://www.google.com/maps/@23.7808875,90.4116751,17z", 23.780888, 90.411675)]
    [InlineData("https://www.google.com/maps/place/Bashundhara+City/@23.75,90.39,17z/data=!3m1!4b1!4m6!3m5!1s0x0:0x0!8m2!3d23.7509!4d90.3904", 23.7509, 90.3904)]
    [InlineData("https://maps.google.com/?q=23.8103,90.4125", 23.8103, 90.4125)]
    [InlineData("https://www.google.com/maps/search/?api=1&query=23.8103%2C90.4125", 23.8103, 90.4125)]
    [InlineData("https://www.google.com/maps/search/23.8103,+90.4125", 23.8103, 90.4125)]
    [InlineData("23°48'37.1\"N 90°24'45.0\"E", 23.810306, 90.4125)]
    public void A_pin_is_read_from_coordinates_or_a_google_maps_link(string text, double latitude, double longitude)
    {
        var pin = GeoPoint.Find(text);

        Assert.NotNull(pin);
        Assert.Equal((decimal)latitude, pin.Latitude);
        Assert.Equal((decimal)longitude, pin.Longitude);
    }

    [Theory]
    [InlineData("")]
    [InlineData("House 12, Road 5, Mirpur")]
    [InlineData("https://maps.app.goo.gl/AbCdEf123")]
    [InlineData("0.0, 0.0")]
    [InlineData("95.1, 90.4")]
    public void Text_without_a_pin_reads_as_none(string text)
    {
        Assert.Null(GeoPoint.Find(text));
    }

    [Fact]
    public void A_pin_needs_both_coordinates_in_range_or_neither()
    {
        Assert.Null(GeoPoint.From(null, null).Value);
        Assert.Equal("parcel.location", GeoPoint.From(23.8m, null).Error?.Code);
        Assert.Equal("parcel.location", GeoPoint.From(91m, 90m).Error?.Code);
        Assert.Equal("23.810331,90.412522", GeoPoint.From(23.8103314m, 90.4125216m).Value!.Text);
    }
}
