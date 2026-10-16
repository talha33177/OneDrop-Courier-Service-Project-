using Microsoft.AspNetCore.WebUtilities;
using Application.Abstractions;
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

/// <summary>
/// What a pasted location holds, for the pin box's "Use it": the coordinates, or the name of a place for the map to
/// look up, or why neither was found. A short link (maps.app.goo.gl) is opened first, Google's hosts only.
/// </summary>
public static class PastedPin
{
    public static async Task<object> ReadAsync(IMapLinks links, string? pasted, CancellationToken cancellationToken)
    {
        var text = pasted?.Trim() ?? "";
        var full = GeoPoint.Find(text) is null ? await links.ExpandAsync(text, cancellationToken) ?? text : text;
        if (GeoPoint.Find(full) is { } pin)
        {
            return new { pin.Latitude, pin.Longitude };
        }

        var place = Uri.TryCreate(full, UriKind.Absolute, out var uri) && uri.Host.Contains("google.", StringComparison.OrdinalIgnoreCase)
            ? QueryHelpers.ParseQuery(uri.Query).GetValueOrDefault("q").FirstOrDefault() ?? PlaceName(uri.AbsolutePath)
            : null;

        return new
        {
            query = place,
            problem = place is null
                ? "That has no location in it. Paste a Google Maps link to the place, or its coordinates, such as 23.8103, 90.4125."
                : null
        };
    }

    /// <summary>The place's name in a link such as /maps/place/Bashundhara+City/…, or null.</summary>
    private static string? PlaceName(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var at = Array.IndexOf(parts, "place");

        return at >= 0 && at + 1 < parts.Length ? Uri.UnescapeDataString(parts[at + 1]).Replace('+', ' ') : null;
    }
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

    /// <summary>
    /// Google Maps directions through every stop of the rider's round in turn, starting where the rider is now: the
    /// last stop is the destination and the others are waypoints. Google's link takes nine waypoints, so a longer
    /// round is cut to the first ten stops (<see cref="MaxRouteStops"/>) and the rider asks again from there. A stop
    /// is its pin where there is one, else its address, which Google looks up. Null for no stops.
    /// </summary>
    public static string? Route(IEnumerable<string> stops)
    {
        var round = stops.Where(stop => !string.IsNullOrWhiteSpace(stop)).Take(MaxRouteStops).ToList();
        if (round.Count == 0)
        {
            return null;
        }

        var link = "https://www.google.com/maps/dir/?api=1&travelmode=driving&destination=" + Uri.EscapeDataString(round[^1]);

        return round.Count == 1
            ? link
            : $"{link}&waypoints={string.Join('|', round[..^1].Select(Uri.EscapeDataString))}";
    }

    /// <summary>Stops one route link can hold: Google takes a destination and nine waypoints.</summary>
    public const int MaxRouteStops = 10;

    /// <summary>A stop on the route: its pin when the door was pinned, else the address for Google to look up.</summary>
    public static string Stop(GeoPoint? pin, string address, string area)
    {
        return pin?.Text ?? string.Join(", ", new[] { address, area, "Bangladesh" }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }
}
