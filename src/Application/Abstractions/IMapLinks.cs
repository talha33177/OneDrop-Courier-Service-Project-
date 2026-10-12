namespace Application.Abstractions;

/// <summary>
/// Follows the short links Google Maps shares (maps.app.goo.gl), which hold no coordinates until opened, so the pin a
/// customer sent the merchant can be read off the link it leads to.
/// </summary>
public interface IMapLinks
{
    /// <summary>The full link a short map link leads to; null when it is not one or cannot be followed.</summary>
    Task<string?> ExpandAsync(string link, CancellationToken cancellationToken = default);
}
