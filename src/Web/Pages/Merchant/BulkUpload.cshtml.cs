using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Abstractions;
using Application.Parcels.BulkImport;

namespace Web.Pages.Merchant;

/// <summary>
/// The merchant books many parcels from a filled-in Excel workbook (the template) or a CSV file. Every row is checked
/// first; nothing is booked until all rows are good, and the page lists the rows to fix.
/// </summary>
public class BulkUploadModel(BulkImportHandler handler, IParcelSheets sheets) : PageModel
{
    public const long MaxBytes = 1024 * 1024;

    private const string WorkbookType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public ImportResult? Result { get; private set; }

    public string? Problem { get; private set; }

    public ParcelTemplate Template { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Template = await handler.TemplateAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(IFormFile? file, CancellationToken cancellationToken)
    {
        Template = await handler.TemplateAsync(cancellationToken);
        if (file is null || file.Length == 0)
        {
            Problem = "Choose the filled-in template (.xlsx) or a CSV file to upload.";
            return Page();
        }

        if (file.Length > MaxBytes)
        {
            Problem = "The file is larger than 1 MB. Split it into smaller files.";
            return Page();
        }

        using var content = new MemoryStream();
        await file.CopyToAsync(content, cancellationToken);
        var imported = await handler.ImportFileAsync(content.ToArray(), cancellationToken);
        if (imported.IsFailure)
        {
            Problem = imported.Error!.Message;
            return Page();
        }

        Result = imported.Value;

        return Page();
    }

    public async Task<IActionResult> OnGetTemplateAsync(CancellationToken cancellationToken)
    {
        return File(sheets.Template(await handler.TemplateAsync(cancellationToken)), WorkbookType, "parcels-template.xlsx");
    }

    public async Task<IActionResult> OnGetCsvTemplateAsync(CancellationToken cancellationToken)
    {
        return File(Encoding.UTF8.GetBytes((await handler.TemplateAsync(cancellationToken)).ToCsv()), "text/csv", "parcels-template.csv");
    }
}
