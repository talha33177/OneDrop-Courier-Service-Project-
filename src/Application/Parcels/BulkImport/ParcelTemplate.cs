using Application.Network.ListAreas;

namespace Application.Parcels.BulkImport;

/// <summary>One column of the upload: its name in a CSV file, its title in the workbook, and what goes in it.</summary>
public sealed record ImportColumn(string Key, string Title, bool Required, string Help);

/// <summary>One row read from a file, by its line (CSV) or row (workbook) number.</summary>
public sealed record SheetRow(int Line, IReadOnlyList<string> Fields);

/// <summary>A delivery speed or kind of contents a file may name, with what the admin says it means.</summary>
public sealed record TemplateChoice(string Name, string? Description, bool IsDefault);

/// <summary>
/// What the downloadable templates are made from, read from the courier's own setup: the columns with what to write in
/// each, and the areas and delivery types the drop-downs offer. The templates hold no sample parcels.
/// </summary>
public sealed record ParcelTemplate(
    IReadOnlyList<ImportColumn> Columns,
    IReadOnlyList<AreaItem> Areas,
    IReadOnlyList<TemplateChoice> Speeds,
    IReadOnlyList<TemplateChoice> Contents,
    int MaxRows)
{
    /// <summary>The plain CSV version: just the column names.</summary>
    public string ToCsv()
    {
        return string.Join(",", Columns.Select(column => column.Key)) + "\r\n";
    }
}
