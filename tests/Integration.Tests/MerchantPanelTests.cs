using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Infrastructure.Email;
using Domain.Merchants;
using Domain.Parcels;

namespace Integration.Tests;

/// <summary>The merchant panel: signing up, booking by hand and in bulk, editing, settings, fraud check, labels.</summary>
public class MerchantPanelTests(WebAppFactory factory) : AppTests(factory)
{
    [Fact]
    public async Task A_shop_signs_up_waits_for_approval_and_books_once_the_admin_approves_it()
    {
        WebAppFactory.RequireDatabase();
        var email = $"{Guid.NewGuid():N}@signup.test";
        var business = $"Sign-up shop {Guid.NewGuid():N}"[..24];
        var areaId = await QueryAsync("onedrop", db => db.Areas.Where(a => a.Name == "Dhanmondi").Select(a => a.Id).SingleAsync(Cancel));

        var visitor = Visit("onedrop");
        var asked = await visitor.SubmitAsync(
            "/Account/Register",
            "/Account/Register",
            ("Input.Business", business),
            ("Input.Owner", "New Owner"),
            ("Input.Phone", NewPhone()),
            ("Input.Email", email),
            ("Input.Password", WebAppFactory.Password),
            ("Input.Address", "Road 27, Dhanmondi"),
            ("Input.PickupAreaId", $"{areaId}"),
            ("Input.PickupAddress", "Road 27, Dhanmondi"));

        // No account until the emailed code is typed; the form waits sealed in the page
        Assert.Contains("We sent a 6-digit code", asked);
        Assert.False(await QueryAsync("onedrop", db => db.Merchants.AnyAsync(m => m.Name == business, Cancel)));
        var pending = Regex.Match(asked, "name=\"pending\" value=\"([^\"]+)\"").Groups[1].Value;
        var sent = Assert.Single(Factory.Services.GetRequiredService<EmailLog>().Recent, e => e.To == email);
        var code = sent.Subject[..6];
        Assert.Matches("^[0-9]{6}$", code);
        Assert.DoesNotContain(WebAppFactory.Password, asked);

        var wrong = await visitor.SubmitAsync("/Account/Register", "/Account/Register?handler=Verify", ("pending", pending), ("code", code == "000000" ? "111111" : "000000"));
        Assert.Contains("4 tries left", wrong);
        // A sealed form that was changed is not believed
        Assert.Contains("That took too long", await visitor.SubmitAsync("/Account/Register", "/Account/Register?handler=Verify", ("pending", pending + "x"), ("code", code)));

        var signedUp = await visitor.PostFormAsync("/Account/Register", "/Account/Register?handler=Verify", ("pending", pending), ("code", $"{code[..3]} {code[3..]}"));
        Assert.Equal(HttpStatusCode.Redirect, signedUp.StatusCode);
        Assert.Equal("/Merchant", signedUp.Headers.Location!.OriginalString);

        var merchant = await SignInAsync("onedrop", email);
        Assert.Contains("waiting for approval", await merchant.PageAsync("/Merchant"));
        Assert.Contains("not active yet", await merchant.SubmitAsync("/Merchant/NewParcel", "/Merchant/NewParcel", Form(await AreaIdAsync("Gulshan 2"))));

        var id = await QueryAsync("onedrop", db => db.Merchants.Where(m => m.Name == business).Select(m => m.Id).SingleAsync(Cancel));
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");
        Assert.Contains(business, await admin.PageAsync("/Admin/Merchants?status=Pending"));
        Assert.Contains("Approved", await admin.SubmitAsync($"/Admin/Merchant/{id}", $"/Admin/Merchant/{id}?handler=Approve"));

        var booked = await merchant.SubmitAsync("/Merchant/NewParcel", "/Merchant/NewParcel", Form(await AreaIdAsync("Gulshan 2")));
        Assert.Contains("is booked for Gulshan 2", booked);
        Assert.Equal(MerchantStatus.Active, await QueryAsync("onedrop", db => db.Merchants.Where(m => m.Id == id).Select(m => m.Status).SingleAsync(Cancel)));
    }

    [Fact]
    public async Task A_signed_up_email_already_used_at_this_courier_is_refused_and_leaves_no_shop()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        var business = $"Duplicate {Guid.NewGuid():N}"[..20];

        var page = await Visit("onedrop").SubmitAsync(
            "/Account/Register",
            "/Account/Register",
            ("Input.Business", business),
            ("Input.Owner", "Owner"),
            ("Input.Phone", NewPhone()),
            ("Input.Email", shop.Email),
            ("Input.Password", WebAppFactory.Password),
            ("Input.Address", "Road 1"),
            ("Input.PickupAreaId", $"{await AreaIdAsync("Mirpur 2")}"),
            ("Input.PickupAddress", "Road 1"));

        Assert.Contains("already has an account", page);
        Assert.False(await QueryAsync("onedrop", db => db.Merchants.AnyAsync(m => m.Name == business, Cancel)));
    }

    [Fact]
    public async Task A_parcel_booked_by_hand_is_edited_and_cancelled_before_pickup_and_another_shop_sees_none_of_it()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        var other = await NewMerchantAsync();
        var merchant = await SignInAsync("onedrop", shop.Email);
        var gulshan = await AreaIdAsync("Gulshan 2");

        var booked = await merchant.SubmitAsync("/Merchant/NewParcel", "/Merchant/NewParcel", Form(gulshan));
        var code = await QueryAsync("onedrop", db => db.Parcels.Where(p => p.MerchantId == shop.Id).Select(p => p.TrackingCode).SingleAsync(Cancel));
        Assert.Contains(code, booked);
        Assert.Contains("Ayesha Siddiqua", await merchant.PageAsync("/Merchant/Parcels"));

        var sylhet = await AreaIdAsync("Sylhet Sadar");
        Assert.Contains("The parcel is updated", await merchant.SubmitAsync($"/Merchant/EditParcel/{code}", $"/Merchant/EditParcel/{code}", Form(sylhet)));
        var edited = await ParcelAsync(code);
        Assert.Equal((Domain.Pricing.ServiceArea.OutsideCity, 120m), (edited.ServiceArea, edited.DeliveryCharge));

        var stranger = await SignInAsync("onedrop", other.Email);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/Merchant/Parcel/{code}")).StatusCode);
        Assert.DoesNotContain(code, await stranger.PageAsync("/Merchant/Parcels"));
        Assert.DoesNotContain(code, await stranger.PageAsync("/Merchant/Labels"));
        Assert.Contains(code, await merchant.PageAsync("/Merchant/Labels"));

        Assert.Contains("is cancelled", await merchant.SubmitAsync($"/Merchant/Parcel/{code}", $"/Merchant/Parcel/{code}?handler=Cancel", ("reason", "Out of stock")));
        Assert.Equal(ParcelStatus.Cancelled, (await ParcelAsync(code)).Status);
    }

    [Fact]
    public async Task A_bulk_upload_books_every_row_or_none_and_names_the_rows_to_fix()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        var merchant = await SignInAsync("onedrop", shop.Email);
        const string header = "invoice,recipient_name,recipient_phone,recipient_address,area,cod_amount,weight_kg,item_description,note\n";

        var bad = await UploadAsync(merchant, header +
            "A-1,Rahim,01811000101,\"House 1, Road 2\",Mirpur 2,1200,0.5,Shirt,\n" +
            "A-2,Karim,12345,House 3,Atlantis,800,0.5,,\n");
        Assert.Contains("Line 3", bad);
        Assert.Contains("Nothing was booked", bad);
        Assert.Equal(0, await QueryAsync("onedrop", db => db.Parcels.CountAsync(p => p.MerchantId == shop.Id, Cancel)));

        var good = await UploadAsync(merchant, header +
            "B-1,Rahim,01811000101,\"House 1, Road 2\",Mirpur 2,1200,0.5,Shirt,Call first\n" +
            "B-2,Karim,01811000102,House 3,Sylhet Sadar,\"1,800\",2.2,Shoes,\n");
        Assert.Contains("2 parcels booked", good);
        var parcels = await QueryAsync("onedrop", db => db.Parcels.Where(p => p.MerchantId == shop.Id).OrderBy(p => p.MerchantReference).ToListAsync(Cancel));
        Assert.Equal(["B-1", "B-2"], parcels.Select(p => p.MerchantReference));
        Assert.Equal((1800m, 2200, 120m + 2 * 20), (parcels[1].CodAmount, parcels[1].WeightGrams, parcels[1].DeliveryCharge));
        Assert.Equal("House 1, Road 2", parcels[0].RecipientAddress);
    }

    [Fact]
    public async Task The_fraud_check_counts_a_phones_parcels_at_every_merchant_and_names_none()
    {
        WebAppFactory.RequireDatabase();
        var phone = NewPhone();
        var shop = await NewMerchantAsync();
        var other = await NewMerchantAsync();
        await BookAsync(other.ApiKey, phone: phone);
        var code = await BookAsync(other.ApiKey, phone: phone);
        await QueryAsync("onedrop", async db =>
        {
            var parcel = await db.Parcels.SingleAsync(p => p.TrackingCode == code, Cancel);
            parcel.ReceiveAt(parcel.PickupHubId, Today);
            parcel.RequestReturn("Refused on the phone");
            parcel.ReturnToMerchant(parcel.PickupHubId, DateTime.UtcNow);

            return await db.SaveChangesAsync(Cancel);
        });

        var merchant = await SignInAsync("onedrop", shop.Email);
        var page = await merchant.PageAsync($"/Merchant/FraudCheck?phone={phone}");

        Assert.Contains("High risk", page);
        Assert.Contains("0% of finished parcels delivered", page);
        Assert.DoesNotContain(other.Name, page);
        Assert.Contains("New customer", await merchant.PageAsync($"/Merchant/FraudCheck?phone={NewPhone()}"));
    }

    [Fact]
    public async Task A_post_to_a_handler_a_page_does_not_have_is_not_found_instead_of_a_broken_page()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        var merchant = await SignInAsync("onedrop", shop.Email);

        var missing = await merchant.PostFormAsync("/Merchant/Settings", "/Merchant/Settings?handler=Nothing");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // A page that has handlers still answers its own, and a plain GET still draws the page
        Assert.Contains("payout account is saved", await merchant.SubmitAsync("/Merchant/Settings", "/Merchant/Settings?handler=Payout", ("method", "Nagad"), ("payoutAccount", "01911222333"), ("accountName", "Test Owner")));
        Assert.Contains("Settings", await merchant.PageAsync("/Merchant/Settings"));
    }

    [Fact]
    public async Task A_merchant_sets_its_payout_account_and_adds_a_pickup_point()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        var merchant = await SignInAsync("onedrop", shop.Email);

        Assert.Contains("Enter the bank account number", await merchant.SubmitAsync("/Merchant/Settings", "/Merchant/Settings?handler=Payout", ("method", "Bank"), ("payoutAccount", "ab"), ("accountName", "Owner")));
        Assert.Contains("payout account is saved", await merchant.SubmitAsync("/Merchant/Settings", "/Merchant/Settings?handler=Payout", ("method", "Nagad"), ("payoutAccount", "01911222333"), ("accountName", "Test Owner")));
        // A pickup point has a page of its own (with the door's pin); saving it lands back on Settings
        Assert.Contains("Pickup point added", await merchant.SubmitAsync("/Merchant/PickupPoint", "/Merchant/PickupPoint", ("name", "Warehouse"), ("areaId", $"{await AreaIdAsync("Tongi")}"), ("address", "Plot 4, Tongi"), ("phone", "01911222333")));

        var saved = await QueryAsync("onedrop", db => db.Merchants.SingleAsync(m => m.Id == shop.Id, Cancel));
        Assert.Equal((PayoutMethod.Nagad, "+8801911222333"), (saved.PayoutMethod!.Value, saved.PayoutAccount!));
        Assert.Equal(2, await QueryAsync("onedrop", db => db.PickupPoints.CountAsync(p => p.MerchantId == shop.Id, Cancel)));
    }

    private Task<long> AreaIdAsync(string area)
    {
        return QueryAsync("onedrop", db => db.Areas.Where(a => a.Name == area).Select(a => a.Id).SingleAsync(Cancel));
    }

    private static (string, string)[] Form(long areaId)
    {
        return
        [
            ("Input.RecipientName", "Ayesha Siddiqua"),
            ("Input.RecipientPhone", "01811000101"),
            ("Input.RecipientAddress", "House 9, Road 3"),
            ("Input.AreaId", $"{areaId}"),
            ("Input.CodAmount", "1500"),
            ("Input.WeightKg", "0.5"),
            ("Input.ItemDescription", "Saree"),
            ("Input.MerchantReference", "FB-1"),
            ("FormKey", Guid.NewGuid().ToString("N"))
        ];
    }

    private static async Task<string> UploadAsync(Visitor merchant, string csv)
    {
        var form = await merchant.GetAsync("/Merchant/BulkUpload");
        var html = await form.Content.ReadAsStringAsync(Cancel);
        var token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        var content = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" }
        };
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "parcels.csv");
        var posted = await merchant.SendAsync(HttpMethod.Post, "/Merchant/BulkUpload", content, Visitor.Cookies(form));
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        return WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync(Cancel));
    }
}
