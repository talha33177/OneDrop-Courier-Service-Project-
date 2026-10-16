using System.Globalization;
using System.Text;
using Application.Abstractions;
using Application.Common;
using Application.Delivery.Pickups;
using Application.Parcels.CreateParcel;
using Domain.Common;
using Domain.Parcels;
using Domain.Pricing;

namespace Application.Parcels.BulkImport;

/// <summary>One row of the upload that could not be booked, by its line number in the file.</summary>
public sealed record ImportProblem(int Line, string Message);

/// <summary>The parcels booked, or the rows to fix: nothing is booked while any row has a problem.</summary>
public sealed record ImportResult(IReadOnlyList<CreateParcelResult> Booked, IReadOnlyList<ImportProblem> Problems);

/// <summary>
/// Books many parcels from a CSV file, as merchants export them from their shop or a spreadsheet. Every row is checked
/// first; only when all are good are they saved, in one go, so a file is never half booked, and their pickup points
/// go on the day's pickup list. Columns, with a header row: <c>invoice, recipient_name, recipient_phone,
/// recipient_address, area, cod_amount, weight_kg</c>, and optionally <c>item_description, note, speed</c> and
/// <c>product_type</c> (a code or name from the courier's delivery types, such as express or fresh food) and <c>location</c> (the door's
/// coordinates, or a Google Maps link that holds them).
/// </summary>
public class BulkImportHandler(IAppDbContext db, CreateParcelHandler parcels, PickupSchedule pickups)
{
    public const int MaxRows = 500;

    public static readonly string[] Columns =
    [
        "invoice", "recipient_name", "recipient_phone", "recipient_address", "area", "cod_amount", "weight_kg",
        "item_description", "note", "speed", "product_type", "location"
    ];

    public async Task<Result<ImportResult>> ImportAsync(string csv, CancellationToken cancellationToken = default)
    {
        var lines = ReadRows(csv);
        if (lines.Count == 0)
        {
            return Error.Validation("import.empty", "The file is empty. Download the template and fill in one parcel per row.");
        }

        var header = lines[0].Fields.Select(field => field.Trim().ToLowerInvariant()).ToList();
        var missing = Columns.Take(7).Where(column => !header.Contains(column)).ToList();
        if (missing.Count > 0)
        {
            return Error.Validation(
                "import.columns",
                $"The header row is missing {string.Join(", ", missing)}. Use the template's columns.");
        }

        var rows = lines.Skip(1).Where(row => row.Fields.Any(field => field.Trim().Length > 0)).ToList();
        if (rows.Count == 0)
        {
            return Error.Validation("import.noRows", "The file has a header but no parcels.");
        }

        if (rows.Count > MaxRows)
        {
            return Error.Validation("import.tooMany", $"Upload at most {MaxRows} parcels at a time.");
        }

        var problems = new List<ImportProblem>();
        var prepared = new List<PreparedParcel>();
        foreach (var row in rows)
        {
            string Field(string column)
            {
                var index = header.IndexOf(column);

                return index >= 0 && index < row.Fields.Count ? row.Fields[index].Trim() : "";
            }

            if (!TryAmount(Field("cod_amount"), out var cod) || !TryAmount(Field("weight_kg"), out var weight))
            {
                problems.Add(new ImportProblem(row.Line, "cod_amount and weight_kg must be numbers, such as 1250 and 0.5."));
                continue;
            }

            var pin = GeoPoint.Find(Field("location"));
            if (pin is null && Field("location").Length > 0)
            {
                problems.Add(new ImportProblem(
                    row.Line,
                    "location needs the door's coordinates, such as 23.8103, 90.4125, or a full Google Maps link with them (a maps.app.goo.gl link has to be opened first)."));
                continue;
            }

            var command = new CreateParcelCommand
            {
                MerchantReference = Field("invoice").NullIfBlank(),
                RecipientName = Field("recipient_name"),
                RecipientPhone = Field("recipient_phone"),
                RecipientAddress = Field("recipient_address"),
                Area = Field("area"),
                CodAmount = cod,
                WeightKg = weight,
                Speed = Field("speed").NullIfBlank(),
                ProductType = Field("product_type").NullIfBlank(),
                RecipientLatitude = pin?.Latitude,
                RecipientLongitude = pin?.Longitude,
                ItemDescription = Field("item_description").NullIfBlank(),
                Note = Field("note").NullIfBlank()
            };
            var parcel = await parcels.PrepareAsync(command, null, cancellationToken);
            if (parcel.IsSuccess)
            {
                prepared.Add(parcel.Value);
            }
            else
            {
                var error = parcel.Error!;
                var detail = error.Fields is { Count: > 0 } fields
                    ? string.Join(" ", fields.Values.SelectMany(messages => messages))
                    : error.Message;
                problems.Add(new ImportProblem(row.Line, detail));
            }
        }

        if (problems.Count > 0)
        {
            return new ImportResult([], problems);
        }

        db.Parcels.AddRange(prepared.Select(p => p.Parcel));
        await db.SaveChangesAsync(cancellationToken);
        var days = await pickups.ScheduleAsync([.. prepared.Select(p => p.Parcel)], cancellationToken);

        return new ImportResult(
            [.. prepared.Select(p => CreateParcelHandler.Describe(p, days.TryGetValue(p.Parcel.PickupPointId, out var day) ? day : null))],
            []);
    }

    /// <summary>The template a merchant downloads: the header and two example rows.</summary>
    public static string Template(string exampleArea)
    {
        return string.Join(",", Columns) + "\r\n" +
            $"INV-1001,Rahim Uddin,01712345678,\"House 12, Road 5\",{exampleArea},1250,0.5,T-shirt,Call before coming,regular,general,\r\n" +
            $"INV-1002,Nadia Islam,01812345678,\"Flat 3B, House 7\",{exampleArea},800,1,Glass vase,,express,fragile,\"23.8103, 90.4125\"\r\n";
    }

    private static bool TryAmount(string text, out decimal value)
    {
        return decimal.TryParse(
            text.Length == 0 ? "0" : text.Replace("৳", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out value);
    }

    /// <summary>Splits CSV into rows of fields: commas, quoted fields with commas, doubled quotes and line breaks inside quotes.</summary>
    private static List<(int Line, List<string> Fields)> ReadRows(string csv)
    {
        var rows = new List<(int, List<string>)>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var line = 1;
        var rowLine = 1;
        var text = csv.TrimStart('﻿');
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    rows.Add((rowLine, fields));
                    fields = [];
                    line++;
                    rowLine = line;
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            rows.Add((rowLine, fields));
        }

        return rows;
    }
}
