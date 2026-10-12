using Domain.Common;

namespace Web.Display;

/// <summary>
/// The Google Maps key (<c>GoogleMaps:ApiKey</c>). It is a browser key, sent to every page that shows a map, so it is
/// restricted to the site's address in Google Cloud; it still never goes into the repository. With none set, pages
/// fall back to links that open Google Maps, which need no key.
/// </summary>
public sealed class MapsOptions
{
    public string? ApiKey { get; set; }

    public bool Configured => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>Links into Google Maps for a pinned door or an address: navigation for the rider, a small map on a page.</summary>
public static class MapLinks
{
    /// <summary>Directions to the pin; on a phone it opens the Google Maps app.</summary>
    public static string Directions(GeoPoint pin)
    {
        return "https://www.google.com/maps/dir/?api=1&destination=" + pin.Text;
    }

    /// <summary>A search for an address, for a door nobody pinned.</summary>
    public static string Search(string address)
    {
        return "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(address);
    }

    /// <summary>The pin on Google's map, to open in a new tab.</summary>
    public static string Show(GeoPoint pin)
    {
        return "https://www.google.com/maps/search/?api=1&query=" + pin.Text;
    }

    /// <summary>A small map of the pin to show in a page (Google's Maps Embed API, which costs nothing).</summary>
    public static string Embed(string apiKey, GeoPoint pin)
    {
        return $"https://www.google.com/maps/embed/v1/place?key={Uri.EscapeDataString(apiKey)}&q={pin.Text}&zoom=16";
    }
}
