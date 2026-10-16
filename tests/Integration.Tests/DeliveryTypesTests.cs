using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Domain.Pricing;

namespace Integration.Tests;

/// <summary>
/// Delivery types and the door on a map: booked through the API and the form, priced from the courier's extras,
/// refused where the route does not take them, and shown to the hub and the rider.
/// </summary>
public class DeliveryTypesTests(WebAppFactory factory) : AppTests(factory)
{
    [Fact]
    public async Task Express_and_fragile_add_their_extras_and_the_pin_reaches_the_rider()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: "Mirpur 10");
        var client = Factory.ClientFor(shop.ApiKey);

        var response = await client.PostAsJsonAsync(
            "/api/v1/parcels",
            new
            {
                RecipientName = "Nadia Islam",
                RecipientPhone = NewPhone(),
                RecipientAddress = "Flat 3B, House 7",
                Area = "Gulshan 2",
                CodAmount = 800,
                WeightKg = 0.5m,
                Speed = "express",
                ProductType = "fragile",
                RecipientLatitude = 23.7925m,
                RecipientLongitude = 90.4078m
            },
            Cancel);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var booked = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Cancel);
        var code = booked.GetProperty("trackingCode").GetString()!;
        Assert.Equal(60 + 40 + 20, booked.GetProperty("deliveryCharge").GetDecimal());
        Assert.Equal(40, booked.GetProperty("speedCharge").GetDecimal());
        Assert.Equal(20, booked.GetProperty("productCharge").GetDecimal());
        Assert.Equal("express", booked.GetProperty("speed").GetString());
        Assert.Equal("fragile", booked.GetProperty("productType").GetString());
        Assert.NotEqual(JsonValueKind.Null, booked.GetProperty("pickupOn").ValueKind);

        var read = await client.GetFromJsonAsync<JsonElement>($"/api/v1/parcels/{code}", Json, Cancel);
        Assert.Equal(23.7925m, read.GetProperty("recipientLatitude").GetDecimal());
        Assert.Equal("fragile", read.GetProperty("productType").GetString());

        // The hub hands it to a rider, who is guided to the pin and told how to carry it
        var rider = await NewRiderAsync("GUL");
        var hub = await SignInAsync("onedrop", "hub@onedrop.test");
        await hub.SubmitAsync("/Hub/Scan?hub=GUL", "/Hub/Scan?mode=Receive&hub=GUL", ("Code", code));
        Assert.Contains("tag-speed", await hub.PageAsync("/Hub/Assign?hub=GUL"));
        await hub.SubmitAsync("/Hub/Assign?hub=GUL", "/Hub/Assign?hub=GUL", ("riderId", $"{rider.Id}"), ("codes", code));
        var door = WebUtility.HtmlDecode(await (await SignInAsync("onedrop", rider.Email)).PageAsync($"/Rider/Delivery/{code}"));
        Assert.Contains("https://www.google.com/maps/dir/?api=1&destination=23.7925,90.4078", door);
        Assert.Contains("Fragile: handle with care", door);
    }

    [Fact]
    public async Task What_a_route_does_not_take_is_refused_and_the_charge_endpoint_says_what_it_offers()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: "Mirpur 10");
        var client = Factory.ClientFor(shop.ApiKey);

        var frozen = await client.PostAsJsonAsync(
            "/api/v1/parcels",
            new { RecipientName = "Karim", RecipientPhone = NewPhone(), RecipientAddress = "Road 1", Area = "Zindabazar", CodAmount = 500, WeightKg = 1m, ProductType = "frozen" },
            Cancel);
        Assert.Equal(HttpStatusCode.BadRequest, frozen.StatusCode);
        Assert.Contains("does not carry frozen parcels outside the city", await frozen.Content.ReadAsStringAsync(Cancel));

        var pinOnly = await client.PostAsJsonAsync(
            "/api/v1/parcels",
            new { RecipientName = "Karim", RecipientPhone = NewPhone(), RecipientAddress = "Road 1", Area = "Gulshan 2", CodAmount = 500, WeightKg = 1m, RecipientLatitude = 23.79m },
            Cancel);
        Assert.Equal(HttpStatusCode.BadRequest, pinOnly.StatusCode);

        var quote = await client.GetFromJsonAsync<JsonElement>(
            "/api/v1/charge?area=Gulshan%202&weightKg=1&codAmount=1000&speed=express&productType=freshFood", Json, Cancel);
        Assert.Equal(60 + 40 + 30, quote.GetProperty("deliveryCharge").GetDecimal());
        Assert.Equal("express", quote.GetProperty("speed").GetString());
        Assert.Equal("fresh-food", quote.GetProperty("productType").GetString());
        Assert.Contains(
            quote.GetProperty("speeds").EnumerateArray(),
            s => s.GetProperty("code").GetString() == "express" && s.GetProperty("charge").GetDecimal() == 40 &&
                s.GetProperty("deliveryDays").GetInt32() == 1);
        Assert.Contains(
            quote.GetProperty("contents").EnumerateArray(),
            p => p.GetProperty("code").GetString() == "frozen" && p.GetProperty("charge").GetDecimal() == 50);
        var far = await client.GetFromJsonAsync<JsonElement>("/api/v1/charge?area=Zindabazar&weightKg=1&codAmount=0", Json, Cancel);
        Assert.DoesNotContain(far.GetProperty("speeds").EnumerateArray(), s => s.GetProperty("code").GetString() == "express");
        Assert.DoesNotContain(far.GetProperty("contents").EnumerateArray(), p => p.GetProperty("code").GetString() == "frozen");

        var unknown = await client.GetAsync("/api/v1/charge?area=Gulshan%202&weightKg=1&codAmount=0&speed=rocket", Cancel);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("regular, express", await unknown.Content.ReadAsStringAsync(Cancel));
    }

    [Fact]
    public async Task The_booking_form_takes_a_speed_contents_and_a_pasted_pin()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync(area: "Mirpur 10");
        var merchant = await SignInAsync("onedrop", shop.Email);
        var areaId = await QueryAsync("onedrop", db => db.Areas.Where(a => a.Name == "Banani").Select(a => a.Id).SingleAsync(Cancel));

        // A short link the customer sent is opened, a link to a named place gives the name to look up, text gives nothing
        var pin = JsonDocument.Parse(await merchant.PageAsync($"/Merchant/NewParcel?handler=Pin&link={Uri.EscapeDataString(ShortMapLinks.Link)}")).RootElement;
        Assert.Equal(23.7509m, pin.GetProperty("latitude").GetDecimal());
        var place = JsonDocument.Parse(await merchant.PageAsync("/Merchant/NewParcel?handler=Pin&link=" + Uri.EscapeDataString("https://www.google.com/maps/place/Bashundhara+City/data=!4m2"))).RootElement;
        Assert.Equal("Bashundhara City", place.GetProperty("query").GetString());
        Assert.NotEqual(JsonValueKind.Null, JsonDocument.Parse(await merchant.PageAsync("/Merchant/NewParcel?handler=Pin&link=House%2012")).RootElement.GetProperty("problem").ValueKind);

        var quote = JsonDocument.Parse(await merchant.PageAsync($"/Merchant/NewParcel?handler=Quote&areaId={areaId}&weightKg=1&codAmount=500&speed=Express&productType=Liquid")).RootElement;
        Assert.Equal(60 + 40 + 10, quote.GetProperty("deliveryCharge").GetDecimal());

        var page = await merchant.SubmitAsync(
            "/Merchant/NewParcel",
            "/Merchant/NewParcel",
            ("Input.RecipientName", "Sadia Rahman"),
            ("Input.RecipientPhone", NewPhone()),
            ("Input.RecipientAddress", "House 5, Road 11"),
            ("Input.AreaId", $"{areaId}"),
            ("Input.CodAmount", "500"),
            ("Input.WeightKg", "1"),
            ("Input.Speed", "Express"),
            ("Input.ProductType", "Liquid"),
            ("Input.RecipientLatitude", "23.7937"),
            ("Input.RecipientLongitude", "90.4066"));

        Assert.Contains("is booked for Banani", WebUtility.HtmlDecode(page));
        var parcel = await QueryAsync("onedrop", db => db.Parcels
            .Include(p => p.Speed)
            .Include(p => p.ProductType)
            .AsNoTracking()
            .SingleAsync(p => p.MerchantId == shop.Id, Cancel));
        Assert.Equal("express", parcel.Speed?.Code);
        Assert.Equal("liquid", parcel.ProductType?.Code);
        Assert.Equal(110, parcel.DeliveryCharge);
        Assert.Equal(90.4066m, parcel.RecipientLongitude);
        var shown = await merchant.PageAsync($"/Merchant/Parcel/{parcel.TrackingCode}");
        Assert.Contains("tag-speed", shown);
        Assert.Contains("Pinned", shown);
    }


    [Fact]
    public async Task The_admin_adds_a_speed_and_contents_merchants_book_with_and_takes_them_off()
    {
        WebAppFactory.RequireDatabase();
        var admin = await SignInAsync("rival", "admin@rival.test");
        var run = Random.Shared.Next(1000, 9999);

        var added = await admin.SubmitAsync(
            "/Admin/DeliveryTypes",
            "/Admin/DeliveryTypes?handler=Add&kind=Speed",
            ("Name", $"Same day {run}"),
            ("Description", "Delivered before 9 pm"),
            ("Icon", "clock"),
            ("GoesFirst", "true"),
            ("OfferedIn", "InsideCity"),
            ("Charges[0]", "80"),
            ("Days[0]", "0"),
            ("Charges[1]", "15"));
        Assert.Contains($"Same day {run} added", added);
        Assert.Contains($"Electronics {run} added", await admin.SubmitAsync(
            "/Admin/DeliveryTypes",
            "/Admin/DeliveryTypes?handler=Add&kind=Contents",
            ("Name", $"Electronics {run}"),
            ("Handling", "Electronics: keep dry"),
            ("Icon", "lock"),
            ("OfferedIn", "InsideCity"),
            ("Charges[0]", "25")));
        Assert.Contains("There is already a delivery speed called", await admin.SubmitAsync(
            "/Admin/DeliveryTypes",
            "/Admin/DeliveryTypes?handler=Add&kind=Speed",
            ("Name", $"same day {run}"),
            ("Icon", "clock")));

        // A merchant books with the new codes, priced from the rival's own rate card and its new extras
        var shop = Factory.ClientFor(WebAppFactory.Rival);
        var booked = await shop.PostAsJsonAsync(
            "/api/v1/parcels",
            new
            {
                RecipientName = "Rina",
                RecipientPhone = NewPhone(),
                RecipientAddress = "Road 2",
                Area = "Rival Town",
                CodAmount = 300,
                WeightKg = 0.5m,
                Speed = $"same-day-{run}",
                ProductType = $"Electronics {run}"
            },
            Cancel);
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var parcel = await booked.Content.ReadFromJsonAsync<JsonElement>(Json, Cancel);
        Assert.Equal(70 + 80 + 25, parcel.GetProperty("deliveryCharge").GetDecimal());
        Assert.Equal($"same-day-{run}", parcel.GetProperty("speed").GetString());
        var code = parcel.GetProperty("trackingCode").GetString()!;
        var label = WebUtility.HtmlDecode(await admin.PageAsync($"/Hub/Labels?codes={code}"));
        Assert.Contains($"Same day {run}", label);
        Assert.Contains("Electronics: keep dry", label);

        // OneDrop Courier sells nothing of the rival's
        var ours = Factory.ClientFor((await NewMerchantAsync(area: "Mirpur 10")).ApiKey);
        var refused = await ours.GetAsync($"/api/v1/charge?area=Gulshan%202&weightKg=1&codAmount=0&speed=same-day-{run}", Cancel);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.DoesNotContain($"Same day {run}", await (await SignInAsync("onedrop", "admin@onedrop.test")).PageAsync("/Admin/DeliveryTypes"));

        // The default stays; the new speed is taken off, refused from then on, and the parcel keeps it
        var options = await QueryAsync("rival", db => db.DeliveryOptions.Include(o => o.Charges).AsNoTracking().ToListAsync(Cancel));
        var regular = options.Single(o => o.Kind == DeliveryOptionKind.Speed && o.IsDefault);
        var sameDay = options.Single(o => o.Code == $"same-day-{run}");
        // Only the area ticked is offered: the suburb's price was typed but not ticked
        Assert.Equal(ServiceArea.InsideCity, Assert.Single(sameDay.Charges).ServiceArea);
        Assert.Contains("cannot be taken off", await admin.SubmitAsync("/Admin/DeliveryTypes", $"/Admin/DeliveryTypes?handler=Archive&id={regular.Id}"));
        Assert.Contains("is no longer offered", await admin.SubmitAsync("/Admin/DeliveryTypes", $"/Admin/DeliveryTypes?handler=Archive&id={sameDay.Id}"));
        await admin.SubmitAsync(
            "/Admin/DeliveryTypes",
            $"/Admin/DeliveryTypes?handler=Archive&id={options.Single(o => o.Code == $"electronics-{run}").Id}");
        var late = await shop.PostAsJsonAsync(
            "/api/v1/parcels",
            new { RecipientName = "Rina", RecipientPhone = NewPhone(), RecipientAddress = "Road 2", Area = "Rival Town", CodAmount = 300, WeightKg = 0.5m, Speed = $"same-day-{run}" },
            Cancel);
        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
        var kept = await shop.GetFromJsonAsync<JsonElement>($"/api/v1/parcels/{code}", Json, Cancel);
        Assert.Equal($"same-day-{run}", kept.GetProperty("speed").GetString());
        Assert.Equal(80, kept.GetProperty("speedCharge").GetDecimal());
    }
}
