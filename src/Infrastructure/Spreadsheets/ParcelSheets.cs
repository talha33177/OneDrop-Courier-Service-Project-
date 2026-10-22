using System.Globalization;
using System.IO.Compression;
using Application.Abstractions;
using Application.Parcels.BulkImport;
using ClosedXML.Excel;
using Domain.Common;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Spreadsheets;

/// <summary>
/// The parcel upload as an Excel workbook (ClosedXML). The template is one sheet to fill in, empty, with a drop-down for
/// the area, speed and contents and a tip on every column; the lists the drop-downs read sit on a second, hidden sheet.
/// A filled-in workbook is read back as plain text rows, so the same checks serve it and a CSV file.
/// </summary>
public class ParcelSheets(ILogger<ParcelSheets> logger) : IParcelSheets
{
    public const string FillIn = "Parcels";

    private const string Lists = "Lists";

    /// <summary>A workbook is a zip: what it unpacks to is capped, so a small file cannot swell in memory.</summary>
    private const long MaxUnpackedBytes = 25 * 1024 * 1024;

    private const int MaxColumns = 50;

    private static readonly XLColor Ink = XLColor.FromHtml("#141820");
    private static readonly XLColor Rule = XLColor.FromHtml("#dfe4ec");

    private static readonly Dictionary<string, double> Widths = new()
    {
        ["invoice"] = 14,
        ["recipient_name"] = 22,
        ["recipient_phone"] = 18,
        ["recipient_address"] = 38,
        ["area"] = 24,
        ["cod_amount"] = 17,
        ["weight_kg"] = 13,
        ["item_description"] = 24,
        ["note"] = 28,
        ["speed"] = 16,
        ["product_type"] = 18,
        ["location"] = 30
    };

    /// <summary>Columns that hold identifiers and map pins, kept as typed so a leading 0 is not lost.</summary>
    private static readonly string[] TextColumns = ["invoice", "recipient_phone", "location"];

    public byte[] Template(ParcelTemplate template)
    {
        using var workbook = new XLWorkbook();
        workbook.Properties.Title = "Parcel upload template";
        var parcels = workbook.AddWorksheet(FillIn);
        var lists = workbook.AddWorksheet(Lists);
        WriteLists(lists, template);
        lists.Visibility = XLWorksheetVisibility.Hidden;
        WriteParcels(parcels, template, lists);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        return stream.ToArray();
    }

    public Result<IReadOnlyList<SheetRow>> Read(Stream workbook)
    {
        try
        {
            using (var archive = new ZipArchive(workbook, ZipArchiveMode.Read, leaveOpen: true))
            {
                if (archive.Entries.Sum(entry => entry.Length) > MaxUnpackedBytes)
                {
                    return Error.Validation("import.workbook.large", "That workbook is too large. Keep only the parcels and upload again.");
                }
            }

            workbook.Position = 0;
            using var book = new XLWorkbook(workbook);
            var sheet = book.Worksheets.FirstOrDefault(s => s.Name.Equals(FillIn, StringComparison.OrdinalIgnoreCase))
                ?? book.Worksheets.First();
            var columns = Math.Min(sheet.LastColumnUsed(XLCellsUsedOptions.Contents)?.ColumnNumber() ?? 0, MaxColumns);
            var rows = new List<SheetRow>();
            foreach (var row in sheet.CellsUsed(XLCellsUsedOptions.Contents).GroupBy(cell => cell.Address.RowNumber).OrderBy(g => g.Key))
            {
                var fields = new string[columns];
                Array.Fill(fields, "");
                foreach (var cell in row.Where(cell => cell.Address.ColumnNumber <= columns))
                {
                    fields[cell.Address.ColumnNumber - 1] = Text(cell);
                }

                rows.Add(new SheetRow(row.Key, fields));
            }

            return rows;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A file we cannot open is for the merchant to fix, not a fault in the app
            logger.LogInformation(ex, "A parcel workbook could not be read.");

            return Error.Validation(
                "import.workbook.unreadable",
                "We could not read that file. Upload the Excel workbook (.xlsx) from the template, or a CSV UTF-8 file.");
        }
    }

    private static string Text(IXLCell cell)
    {
        var value = cell.Value;

        return value.Type switch
        {
            XLDataType.Text => value.GetText(),
            XLDataType.Number => value.GetNumber().ToString("R", CultureInfo.InvariantCulture),
            XLDataType.Boolean => value.GetBoolean() ? "TRUE" : "FALSE",
            XLDataType.DateTime => value.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => ""
        };
    }

    private static void WriteParcels(IXLWorksheet sheet, ParcelTemplate template, IXLWorksheet lists)
    {
        sheet.SheetView.FreezeRows(1);
        var last = template.MaxRows + 1;
        for (var i = 0; i < template.Columns.Count; i++)
        {
            var column = template.Columns[i];
            var number = i + 1;
            sheet.Column(number).Width = Widths.GetValueOrDefault(column.Key, 20);
            if (TextColumns.Contains(column.Key))
            {
                sheet.Column(number).Style.NumberFormat.Format = "@";
            }

            var head = sheet.Cell(1, number);
            head.Value = column.Required ? $"{column.Title} *" : column.Title;
            head.Style.Font.Bold = true;
            head.Style.Font.FontColor = XLColor.White;
            head.Style.Fill.BackgroundColor = Ink;
            head.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            head.Style.Alignment.Indent = 1;

            var tip = sheet.Range(2, number, last, number).CreateDataValidation();
            tip.ShowInputMessage = true;
            tip.InputTitle = Clip(column.Title, 32);
            tip.InputMessage = Clip(column.Help, 255);
            AddRule(column.Key, tip, template, lists);
        }

        sheet.Row(1).Height = 30;
        var body = sheet.Range(2, 1, last, template.Columns.Count);
        body.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        body.Style.Border.InsideBorderColor = Rule;
        body.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        body.Style.Border.OutsideBorderColor = Rule;
    }

    /// <summary>The check a cell carries: a list to pick from, or a number that makes sense.</summary>
    private static void AddRule(string key, IXLDataValidation rule, ParcelTemplate template, IXLWorksheet lists)
    {
        rule.IgnoreBlanks = true;
        rule.ShowErrorMessage = true;
        rule.ErrorStyle = XLErrorStyle.Stop;
        switch (key)
        {
            case "area" when template.Areas.Count > 0:
                rule.List(lists.Range(2, 1, template.Areas.Count + 1, 1), true);
                rule.ErrorTitle = "Pick an area from the list";
                rule.ErrorMessage = "Choose the recipient's area from the drop-down list.";
                break;
            case "speed" when template.Speeds.Count > 0:
                rule.List(lists.Range(2, 3, template.Speeds.Count + 1, 3), true);
                rule.ErrorTitle = "Pick a speed from the list";
                rule.ErrorMessage = "Choose a speed from the drop-down list, or leave the cell empty.";
                break;
            case "product_type" when template.Contents.Count > 0:
                rule.List(lists.Range(2, 5, template.Contents.Count + 1, 5), true);
                rule.ErrorTitle = "Pick from the list";
                rule.ErrorMessage = "Choose what is inside from the drop-down list, or leave the cell empty.";
                break;
            case "cod_amount":
                rule.Decimal.EqualOrGreaterThan(0);
                rule.ErrorTitle = "Write a number";
                rule.ErrorMessage = "Write the cash to collect as a number. Write 0 if the customer has already paid.";
                break;
            case "weight_kg":
                rule.Decimal.GreaterThan(0);
                rule.ErrorTitle = "Write a weight";
                rule.ErrorMessage = "Write the weight in kilograms as a number above 0.";
                break;
            default:
                rule.ShowErrorMessage = false;
                break;
        }
    }

    /// <summary>What the drop-downs offer: the areas, the speeds and the kinds of contents, one list to a column.</summary>
    private static void WriteLists(IXLWorksheet sheet, ParcelTemplate template)
    {
        sheet.Cell(1, 1).Value = "Area";
        sheet.Cell(1, 3).Value = "Speed";
        sheet.Cell(1, 5).Value = "Product type";
        for (var i = 0; i < template.Areas.Count; i++)
        {
            sheet.Cell(i + 2, 1).Value = template.Areas[i].Name;
        }

        for (var i = 0; i < template.Speeds.Count; i++)
        {
            sheet.Cell(i + 2, 3).Value = template.Speeds[i].Name;
        }

        for (var i = 0; i < template.Contents.Count; i++)
        {
            sheet.Cell(i + 2, 5).Value = template.Contents[i].Name;
        }
    }

    /// <summary>Excel cuts a tip at 32 characters (its title) or 255 (its text) and refuses a longer one.</summary>
    private static string Clip(string text, int limit)
    {
        return text.Length <= limit ? text : text[..(limit - 1)] + "…";
    }
}
