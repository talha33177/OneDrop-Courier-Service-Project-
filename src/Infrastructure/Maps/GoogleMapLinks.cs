using Microsoft.Extensions.Logging;
using Application.Abstractions;

namespace Infrastructure.Maps;

/// <summary>
/// Opens Google Maps' short links by reading where they redirect, without following them anywhere else: only
/// maps.app.goo.gl and goo.gl/maps are asked, over HTTPS, a few hops at most, so a pasted link can never make the server
/// call an address of the merchant's choosing.
/// </summary>
public sealed class GoogleMapLinks(HttpClient http, ILogger<GoogleMapLinks> logger) : IMapLinks
{
    private const int MaxHops = 4;

    public async Task<string?> ExpandAsync(string link, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri) || !IsShort(uri))
        {
            return null;
        }

        var current = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        try
        {
            for (var hop = 0; hop < MaxHops && IsShort(current); hop++)
            {
                using var response = await http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.Headers.Location is not { } next)
                {
                    return null;
                }

                current = next.IsAbsoluteUri ? next : new Uri(current, next);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Could not open the map link {Link}", uri);

            return null;
        }

        return IsShort(current) ? null : current.ToString();
    }

    private static bool IsShort(Uri uri)
    {
        return uri.Scheme is "https" or "http" &&
            (uri.Host.Equals("maps.app.goo.gl", StringComparison.OrdinalIgnoreCase) ||
                (uri.Host.Equals("goo.gl", StringComparison.OrdinalIgnoreCase) &&
                    uri.AbsolutePath.StartsWith("/maps", StringComparison.OrdinalIgnoreCase)));
    }
}
