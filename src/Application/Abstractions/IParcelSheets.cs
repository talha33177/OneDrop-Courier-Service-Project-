using Application.Parcels.BulkImport;
using Domain.Common;

namespace Application.Abstractions;

/// <summary>The Excel workbook a merchant fills in to book many parcels, and the filled-in workbook read back.</summary>
public interface IParcelSheets
{
    /// <summary>The workbook to download: a guide, the sheet to fill in, and the lists its drop-downs read.</summary>
    byte[] Template(ParcelTemplate template);

    /// <summary>The rows of the sheet a merchant filled in (the one named Parcels, else the first), as text.</summary>
    Result<IReadOnlyList<SheetRow>> Read(Stream workbook);
}
