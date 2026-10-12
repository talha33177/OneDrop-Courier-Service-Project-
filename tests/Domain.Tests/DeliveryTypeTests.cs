using Domain.Common;
using Domain.Parcels;
using Domain.Pricing;

namespace Domain.Tests;

/// <summary>Delivery types: express and what is inside, their extras on the rate card, and the door pinned on a map.</summary>
public class DeliveryTypeTests
{
    [Fact]
    public void Express_and_what_is_inside_add_their_extras_to_the_delivery_charge()
    {
        var charges = Build.RateWithExtras(ServiceArea.Suburb).Price(800, DeliverySpeed.Express, ProductType.Fragile).Value;

        Assert.Equal(160, charges.DeliveryCharge);
        Assert.Equal(40, charges.SpeedCharge);
        Assert.Equal(20, charges.ProductCharge);
        Assert.Equal(1, charges.DeliveryDays);
        Assert.Equal(DeliverySpeed.Express, charges.Speed);
        Assert.Equal(ProductType.Fragile, charges.ProductType);
    }

    [Fact]
    public void Frozen_costs_more_than_fragile_and_fragile_more_than_regular()
    {
        var rate = Build.RateWithExtras(ServiceArea.InsideCity);
        decimal Charge(ProductType product) => rate.Price(500, DeliverySpeed.Regular, product).Value.DeliveryCharge;

        Assert.Equal(60, Charge(ProductType.General));
        Assert.True(Charge(ProductType.Fragile) > Charge(ProductType.General));
        Assert.True(Charge(ProductType.Frozen) > Charge(ProductType.Fragile));
        Assert.Equal(1, rate.Price(500, DeliverySpeed.Regular, ProductType.General).Value.DeliveryDays);
    }

    [Fact]
    public void What_a_service_area_does_not_offer_is_refused_with_the_reason()
    {
        var outside = Build.RateWithExtras(ServiceArea.OutsideCity);

        var express = outside.Price(500, DeliverySpeed.Express, ProductType.General).Error!;
        Assert.Equal("parcel.speed", express.Code);
        Assert.Equal("Express delivery is not offered outside the city. Choose regular delivery.", express.Message);
        var frozen = outside.Price(500, DeliverySpeed.Regular, ProductType.Frozen).Error!;
        Assert.Equal("parcel.productType", frozen.Code);
        Assert.Equal("The courier does not carry frozen parcels outside the city.", frozen.Message);
        Assert.Equal(140 + 20, outside.Price(1500, DeliverySpeed.Regular, ProductType.Fragile).Value.DeliveryCharge);
        Assert.Equal("parcel.speed", outside.Price(500, (DeliverySpeed)9, ProductType.General).Error?.Code);
    }

    [Fact]
    public void A_rate_card_with_no_extras_offers_regular_general_goods_only()
    {
        var rate = Build.Rate(ServiceArea.InsideCity);

        Assert.True(rate.Price(500, DeliverySpeed.Regular, ProductType.General).IsSuccess);
        Assert.False(rate.Offers(DeliverySpeed.Express));
        Assert.Null(rate.ExtraFor(ProductType.Liquid));
    }

    [Theory]
    [InlineData(-1, null, 10, "rate.extra")]
    [InlineData(null, 1, 10, "rate.expressDays")]
    [InlineData(40, 2, 10, "rate.expressDays")]
    [InlineData(40, -1, 10, "rate.expressDays")]
    [InlineData(40, 1, -5, "rate.extra")]
    public void Extras_outside_their_limits_are_refused_and_nothing_changes(int? express, int? days, int liquid, string code)
    {
        var rate = Build.RateWithExtras(ServiceArea.InsideCity);

        Assert.Equal(code, rate.ChangeExtras(new RateExtras(express, days, liquid, 20, 30, 50)).Error?.Code);
        Assert.Equal(40, rate.ExpressCharge);
        Assert.Equal(10, rate.LiquidCharge);
    }

    [Fact]
    public void An_extra_left_empty_is_no_longer_offered_and_parcels_booked_keep_theirs()
    {
        var rate = Build.RateWithExtras(ServiceArea.InsideCity);
        var booked = rate.Price(500, DeliverySpeed.Express, ProductType.Frozen).Value;

        Assert.True(rate.ChangeExtras(Build.NearExtras with { ExpressCharge = null, ExpressDays = null, FrozenCharge = null }).IsSuccess);
        Assert.Equal("parcel.speed", rate.Price(500, DeliverySpeed.Express, ProductType.General).Error?.Code);
        Assert.Equal("parcel.productType", rate.Price(500, DeliverySpeed.Regular, ProductType.Frozen).Error?.Code);
        Assert.Equal(150, booked.DeliveryCharge);
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
                Build.RateWithExtras(ServiceArea.InsideCity).Price(500, DeliverySpeed.Express, ProductType.Frozen).Value)
            {
                Pin = GeoPoint.Find("23.8103, 90.4125")
            })).Value;

        Assert.Equal(150, parcel.DeliveryCharge);
        Assert.Equal(DeliverySpeed.Express, parcel.Speed);
        Assert.Equal(ProductType.Frozen, parcel.ProductType);
        Assert.True(parcel.IsPriority);
        Assert.Equal(23.8103m, parcel.RecipientLatitude);
        Assert.Equal(90.4125m, parcel.Pin?.Longitude);

        parcel.PickUp(Build.Today);
        Assert.Equal(Build.Today.AddDays(1), parcel.DueOn);
        Assert.True(parcel.Reweigh(2_500, Build.Mirpur).IsSuccess);
        Assert.Equal(60 + 2 * 15 + 40 + 50, parcel.DeliveryCharge);
        Assert.Equal(40, parcel.Charges.SpeedCharge);
    }

    [Fact]
    public void A_regular_parcel_of_general_goods_is_not_a_priority()
    {
        var parcel = Build.Parcel();

        Assert.False(parcel.IsPriority);
        Assert.Null(parcel.Pin);
        Assert.True(DeliveryOptions.IsPriority(DeliverySpeed.Regular, ProductType.FreshFood));
        Assert.False(DeliveryOptions.IsPriority(DeliverySpeed.Regular, ProductType.Fragile));
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
