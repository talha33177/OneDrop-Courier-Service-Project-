using Microsoft.EntityFrameworkCore;
using Application.Abstractions;

namespace Application.Network.ListAreas;

public sealed record AreaItem(long Id, string Name, string Zone, string City, bool Suburb, string Hub, string? Division = null);

/// <summary>The areas the courier delivers to, for address forms and GET /api/v1/areas.</summary>
public class ListAreasHandler(IAppDbContext db)
{
    public async Task<IReadOnlyList<AreaItem>> HandleAsync(CancellationToken cancellationToken = default)
    {
        return await db.Areas
            .Where(a => !a.Archived)
            .OrderBy(a => a.Zone!.City)
            .ThenBy(a => a.Name)
            .Select(a => new AreaItem(a.Id, a.Name, a.Zone!.Name, a.Zone.City, a.Zone.IsSuburb, a.Zone.Hub!.Name, a.Zone.Division))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }
}
