using System.Globalization;
using System.Text;
using Application.Abstractions;
using Application.Common;
using Application.Delivery.Pickups;
using Application.Network.ListAreas;
using Application.Parcels.CreateParcel;
using Application.Pricing.DeliveryOptions;
using Domain.Common;
using Domain.Parcels;
using Domain.Pricing;

namespace Application.Parcels.BulkImport;

/// <summary>One row of the upload that could not be booked, by its line number in the file.</summary>
public sealed record ImportProblem(int Line, string Message);

/// <summary>The parcels booked, or the rows to fix: nothing is booked while any row has a problem.</summary>
public sealed record ImportResult(IReadOnlyList<CreateParcelResult> Booked, IReadOnlyList<ImportProblem> Problems);

/// <summary>
/// Books many parcels from a filled-in Excel workbook or a CSV file, as merchants export them from their shop or a
/// spreadsheet. Every row is checked first; only when all are good are they saved, in one go, so a file is never half
/// booked, and their pickup points go on the day's pickup list. Columns, with a heading row: <c>invoice,
/// recipient_name, recipient_phone, recipient_address, area, cod_amount, weight_kg</c>, and optionally
/// <c>item_description, note, speed</c> and <c>product_type</c> (a code or name from the courier's delivery types, such
/// as express or fresh food) and <c>location</c> (the door's coordinates, or a Google Maps link that holds them). A
/// heading may be written as the template titles it ("Recipient name *", "Weight (kg)"): case, spaces and
/// punctuation do not matter.
/// </summary>
public class BulkImportHandler(
    IAppDbContext db,
    CreateParcelHandler parcels,
    PickupSchedule pickups,
    ListAreasHandler areas,
    DeliveryOptionsHandler options,
    IParcelSheets sheets)
{
    public const int MaxRows = 500;

    public static readonly string[] Columns =
    [
        "invoice", "recipient_name", "recipient_phone", "recipient_address", "area", "cod_amount", "weight_kg",
        "item_description", "note", "speed", "product_type", "location"
    ];

    private static readonly string[] OptionalColumns = ["invoice", "item_description", "note", "speed", "product_type", "location"];

    /// <summary>The courier's areas and delivery types with the columns' guide, for the templates and the page.</summary>
    public async Task<ParcelTemplate> TemplateAsync(CancellationToken cancellationToken = default)
    {
        var places = await areas.HandleAsync(cancellationToken);
        var everyOption = await options.ListAsync(cancellationToken: cancellationToken);
        var speeds = Choices(everyOption, DeliveryOptionKind.Speed);
        var contents = Choices(everyOption, DeliveryOptionKind.Contents);

        return new ParcelTemplate(
            Guide(speeds, contents),
            [.. places.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)],
            speeds,
            contents,
            MaxRows);
    }

    /// <summary>Books a file a merchant uploaded: a workbook (a zip, which starts with "PK") or CSV text.</summary>
    public async Task<Result<ImportResult>> ImportFileAsync(byte[] file, CancellationToken cancellationToken = default)
    {
        if (file is [(byte)'P', (byte)'K', ..])
        {
            using var workbook = new MemoryStream(file);
            var sheet = sheets.Read(workbook);

            return sheet.IsFailure ? sheet.Error! : await ImportAsync(sheet.Value, cancellationToken);
        }

        return await ImportAsync(Encoding.UTF8.GetString(file), cancellationToken);
    }

    public async Task<Result<ImportResult>> ImportAsync(string csv, CancellationToken cancellationToken = default)
    {
        return await ImportAsync(ReadRows(csv), cancellationToken);
    }

    public async Task<Result<ImportResult>> ImportAsync(IReadOnlyList<SheetRow> lines, CancellationToken cancellationToken = default)
    {
        if (lines.Count == 0)
        {
            return Error.Validation("import.empty", "The file is empty. Download the template and fill in one parcel per row.");
        }

        var header = lines[0].Fields.Select(Normalize).ToList();
        var missing = Columns.Take(7).Where(column => !header.Contains(column)).ToList();
        if (missing.Count > 0)
        {
            return Error.Validation(
                "import.columns",
                $"The heading row is missing {string.Join(", ", missing)}. Use the template's columns.");
        }

        var rows = lines.Skip(1).Where(row => row.Fields.Any(field => field.Trim().Length > 0)).ToList();
        if (rows.Count == 0)
        {
            return Error.Validation("import.noRows", "The file has a heading row but no parcels.");
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

    private static List<TemplateChoice> Choices(IReadOnlyList<OptionRow> everyOption, DeliveryOptionKind kind)
    {
        return
        [
            .. everyOption
                .Where(o => o.Kind == kind)
                .Select(o => new TemplateChoice(o.Name, o.Description, o.IsDefault))
        ];
    }

    /// <summary>What each column holds, in the words the template and the upload page share.</summary>
    private static List<ImportColumn> Guide(IReadOnlyList<TemplateChoice> speeds, IReadOnlyList<TemplateChoice> contents)
    {
        string[] help =
        [
            "Your own order number, so you can match the parcel to your order later.",
            "Who receives the parcel.",
            "The recipient's mobile number. The rider calls it at the door.",
            "House, road and locality, written as the rider should read it.",
            "The recipient's area, picked from the drop-down list. It decides the delivery charge.",
            "The cash the rider collects at the door, in taka. Write 0 if the customer has already paid.",
            "The parcel's weight in kilograms.",
            "What is inside, in a few words.",
            "Anything the rider should know.",
            $"{Pick(speeds)} Some speeds add to the delivery charge.",
            $"{Pick(contents)} Some kinds add to the delivery charge.",
            "The door's map pin: its coordinates, or a full Google Maps link."
        ];
        string[] titles =
        [
            "Invoice", "Recipient name", "Recipient phone", "Recipient address", "Area", "COD amount (৳)", "Weight (kg)",
            "Item description", "Note", "Speed", "Product type", "Location"
        ];

        return
        [
            .. Columns.Select((key, index) => new ImportColumn(key, titles[index], !OptionalColumns.Contains(key), help[index]))
        ];
    }

    private static string Pick(IReadOnlyList<TemplateChoice> choices)
    {
        var usual = choices.FirstOrDefault(c => c.IsDefault);

        return $"Optional. Pick one: {string.Join(", ", choices.Select(c => c.Name))}." +
            (usual is null ? "" : $" Left empty, it is {usual.Name}.");
    }

    /// <summary>A heading as the code knows it: "Recipient name *" and "recipient_name" are the same column.</summary>
    private static string Normalize(string heading)
    {
        var key = new StringBuilder(heading.Length);
        foreach (var c in heading.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                key.Append(c);
            }
            else if (key.Length > 0 && key[^1] != '_')
            {
                key.Append('_');
            }
        }

        return key.ToString().TrimEnd('_');
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
    private static List<SheetRow> ReadRows(string csv)
    {
        var rows = new List<SheetRow>();
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
                    rows.Add(new SheetRow(rowLine, fields));
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
            rows.Add(new SheetRow(rowLine, fields));
        }

        return rows;
    }
}
