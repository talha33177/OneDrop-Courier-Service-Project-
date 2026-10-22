using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Domain.Pricing;

namespace Integration.Tests;

/// <summary>The bulk upload: the Excel template a merchant downloads, and a filled-in workbook or CSV read back.</summary>
public class BulkUploadTests(WebAppFactory factory) : AppTests(factory)
{
    private const string Workbook = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    [Fact]
    public async Task The_template_is_one_empty_sheet_with_drop_downs_and_no_sample_parcels()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        var merchant = await SignInAsync("onedrop", shop.Email);

        var download = await merchant.GetAsync("/Merchant/BulkUpload?handler=Template");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(Workbook, download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("parcels-template.xlsx", download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var bytes = await download.Content.ReadAsByteArrayAsync(Cancel);

        using var book = new XLWorkbook(new MemoryStream(bytes));
        Assert.Equal(["Parcels", "Lists"], book.Worksheets.Select(s => s.Name));

        var parcels = book.Worksheet("Parcels");
        Assert.Equal(
            ["Invoice", "Recipient name *", "Recipient phone *", "Recipient address *", "Area *", "COD amount (৳) *", "Weight (kg) *", "Item description", "Note", "Speed", "Product type", "Location"],
            Enumerable.Range(1, 12).Select(column => parcels.Cell(1, column).GetString()));
        Assert.Equal("@", parcels.Cell("C2").Style.NumberFormat.Format);
        Assert.Equal(XLAllowedValues.List, parcels.Cell("E2").GetDataValidation()?.AllowedValues);
        Assert.StartsWith("Lists!", parcels.Cell("E2").GetDataValidation()!.Value);
        Assert.Equal(XLAllowedValues.List, parcels.Cell("J2").GetDataValidation()?.AllowedValues);
        Assert.Equal(XLAllowedValues.List, parcels.Cell("K2").GetDataValidation()?.AllowedValues);
        Assert.Equal(XLAllowedValues.Decimal, parcels.Cell("F2").GetDataValidation()?.AllowedValues);
        Assert.Contains("mobile number", parcels.Cell("C2").GetDataValidation()!.InputMessage);
        Assert.Equal(XLWorksheetVisibility.Hidden, book.Worksheet("Lists").Visibility);
        Assert.DoesNotContain(parcels.CellsUsed(), c => c.Address.RowNumber > 1);

        var areas = await QueryAsync("onedrop", db => db.Areas.Where(a => !a.Archived).Select(a => a.Name).ToListAsync(Cancel));
        var listed = book.Worksheet("Lists").Column(1).CellsUsed().Skip(1).Select(c => c.GetString()).ToList();
        Assert.Equal(areas.Order(StringComparer.OrdinalIgnoreCase), listed);

        var types = book.Worksheet("Lists");
        var options = await QueryAsync("onedrop", db => db.DeliveryOptions.Where(o => !o.Archived).OrderBy(o => o.SortOrder).ThenBy(o => o.Id).Select(o => new { o.Kind, o.Name }).ToListAsync(Cancel));
        Assert.Equal(options.Where(o => o.Kind == DeliveryOptionKind.Speed).Select(o => o.Name), types.Column(3).CellsUsed().Skip(1).Select(c => c.GetString()));
        Assert.Equal(options.Where(o => o.Kind == DeliveryOptionKind.Contents).Select(o => o.Name), types.Column(5).CellsUsed().Skip(1).Select(c => c.GetString()));

        var csv = await merchant.GetAsync("/Merchant/BulkUpload?handler=CsvTemplate");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.Equal("invoice,recipient_name,recipient_phone,recipient_address,area,cod_amount,weight_kg,item_description,note,speed,product_type,location\r\n", await csv.Content.ReadAsStringAsync(Cancel));
        var page = WebUtility.HtmlDecode(await merchant.PageAsync("/Merchant/BulkUpload"));
        Assert.Contains("Download the Excel template", page);
        Assert.Contains("What goes in each column", page);
    }

    [Fact]
    public async Task A_filled_in_workbook_books_every_row_or_none_and_names_the_rows_to_fix()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        var merchant = await SignInAsync("onedrop", shop.Email);
        var template = await (await merchant.GetAsync("/Merchant/BulkUpload?handler=Template")).Content.ReadAsByteArrayAsync(Cancel);
        var fastest = await QueryAsync("onedrop", db => db.DeliveryOptions
            .Where(o => !o.Archived && o.Kind == DeliveryOptionKind.Speed && !o.IsDefault)
            .OrderBy(o => o.SortOrder)
            .Select(o => o.Name)
            .FirstAsync(Cancel));

        var bad = await UploadAsync(merchant, Fill(template, "Mirpur 2", "Atlantis", fastest), "parcels.xlsx", Workbook);
        Assert.Contains("Line 3", bad);
        Assert.DoesNotContain("Line 2", bad);
        Assert.Contains("Nothing was booked", bad);
        Assert.Equal(0, await QueryAsync("onedrop", db => db.Parcels.CountAsync(p => p.MerchantId == shop.Id, Cancel)));

        var good = await UploadAsync(merchant, Fill(template, "Mirpur 2", "Gulshan 2", fastest), "parcels.xlsx", Workbook);
        Assert.Contains("2 parcels booked", good);
        var parcels = await QueryAsync("onedrop", db => db.Parcels.Include(p => p.Speed).Where(p => p.MerchantId == shop.Id).OrderBy(p => p.MerchantReference).ToListAsync(Cancel));
        Assert.Equal(["W-1", "W-2"], parcels.Select(p => p.MerchantReference));
        Assert.Equal(["+8801811000101", "+8801811000102"], parcels.Select(p => p.RecipientPhone));
        Assert.Equal((1200m, 500), (parcels[0].CodAmount, parcels[0].WeightGrams));
        Assert.Equal((1800m, 2200), (parcels[1].CodAmount, parcels[1].WeightGrams));
        Assert.Equal(fastest, parcels[1].Speed!.Name);
        Assert.Equal("House 1, Road 2", parcels[0].RecipientAddress);
    }

    [Fact]
    public async Task A_file_that_is_neither_a_workbook_nor_text_with_the_columns_is_refused_with_a_reason()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        var merchant = await SignInAsync("onedrop", shop.Email);

        var broken = await UploadAsync(merchant, [.. "PK"u8, .. new byte[200]], "parcels.xlsx", Workbook);
        Assert.Contains("We could not read that file", broken);

        using var book = new XLWorkbook();
        book.AddWorksheet("Parcels").Cell("A1").Value = "Name";
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        var wrong = await UploadAsync(merchant, stream.ToArray(), "parcels.xlsx", Workbook);
        Assert.Contains("The heading row is missing", wrong);
        Assert.Equal(0, await QueryAsync("onedrop", db => db.Parcels.CountAsync(p => p.MerchantId == shop.Id, Cancel)));
    }

    [Fact]
    public async Task A_csv_whose_headings_are_the_templates_titles_is_read_like_one_with_the_plain_names()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        var merchant = await SignInAsync("onedrop", shop.Email);

        var booked = await UploadAsync(
            merchant,
            Encoding.UTF8.GetBytes(
                "Invoice,Recipient name *,Recipient phone *,Recipient address *,Area *,COD amount (৳) *,Weight (kg) *\r\n" +
                "C-1,Rahim,01811000103,\"House 1, Road 2\",Mirpur 2,900,0.5\r\n"),
            "parcels.csv",
            "text/csv");

        Assert.Contains("1 parcels booked", booked);
        Assert.Equal(900m, await QueryAsync("onedrop", db => db.Parcels.Where(p => p.MerchantId == shop.Id).Select(p => p.CodAmount).SingleAsync(Cancel)));
    }

    /// <summary>The template with two parcels written into its Parcels sheet, as a merchant would type them.</summary>
    private static byte[] Fill(byte[] template, string firstArea, string secondArea, string speed)
    {
        using var book = new XLWorkbook(new MemoryStream(template));
        var sheet = book.Worksheet("Parcels");
        sheet.Cell("A2").Value = "W-1";
        sheet.Cell("B2").Value = "Rahim";
        sheet.Cell("C2").Value = "01811000101";
        sheet.Cell("D2").Value = "House 1, Road 2";
        sheet.Cell("E2").Value = firstArea;
        sheet.Cell("F2").Value = 1200;
        sheet.Cell("G2").Value = 0.5;
        sheet.Cell("A3").Value = "W-2";
        sheet.Cell("B3").Value = "Karim";
        sheet.Cell("C3").Value = 1811000102;
        sheet.Cell("D3").Value = "House 3";
        sheet.Cell("E3").Value = secondArea;
        sheet.Cell("F3").Value = "1,800";
        sheet.Cell("G3").Value = 2.2;
        sheet.Cell("J3").Value = speed;
        using var stream = new MemoryStream();
        book.SaveAs(stream);

        return stream.ToArray();
    }

    private static async Task<string> UploadAsync(Visitor merchant, byte[] file, string name, string type)
    {
        var form = await merchant.GetAsync("/Merchant/BulkUpload");
        var html = await form.Content.ReadAsStringAsync(Cancel);
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        var content = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" }
        };
        var part = new ByteArrayContent(file);
        part.Headers.ContentType = new MediaTypeHeaderValue(type);
        content.Add(part, "file", name);
        var posted = await merchant.SendAsync(HttpMethod.Post, "/Merchant/BulkUpload", content, Visitor.Cookies(form));
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        return WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync(Cancel));
    }
}
