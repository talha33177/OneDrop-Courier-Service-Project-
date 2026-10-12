using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Domain.Common;

/// <summary>
/// A point on the map, in degrees, kept to six decimal places (about 10 cm): the recipient's door, pinned when a
/// parcel is booked so the rider can be guided to it.
/// </summary>
public sealed partial record GeoPoint
{
    private GeoPoint(decimal latitude, decimal longitude)
    {
        Latitude = latitude;
        Longitude = longitude;
    }

    public decimal Latitude { get; }

    public decimal Longitude { get; }

    /// <summary>"23.810331,90.412521", the form map links and the API use.</summary>
    public string Text => string.Create(CultureInfo.InvariantCulture, $"{Latitude:0.######},{Longitude:0.######}");

    /// <summary>A point from its two coordinates, or none when both are missing. One without the other is refused.</summary>
    public static Result<GeoPoint?> From(decimal? latitude, decimal? longitude)
    {
        if (latitude is null && longitude is null)
        {
            return Result.Success<GeoPoint?>(null);
        }

        if (latitude is not { } lat || longitude is not { } lng || lat is < -90 or > 90 || lng is < -180 or > 180 ||
            (lat == 0 && lng == 0))
        {
            return Error.Validation(
                "parcel.location",
                "The pin needs a latitude between -90 and 90 and a longitude between -180 and 180, such as 23.8103, 90.4125.");
        }

        return new GeoPoint(Math.Round(lat, 6, MidpointRounding.AwayFromZero), Math.Round(lng, 6, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// The point in what a merchant pastes: coordinates ("23.8103, 90.4125", or 23°48'37.1"N 90°24'45.0"E as Google
    /// Maps writes a dropped pin) or a Google Maps link that carries them. Null when there are none: a short link
    /// (maps.app.goo.gl) has to be opened first, and a link to a named place may hold no coordinates at all.
    /// </summary>
    public static GeoPoint? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2000)
        {
            return null;
        }

        var decoded = WebUtility.UrlDecode(text.Trim());
        foreach (var pattern in (Regex[])[PlacePin(), Parameter(), Viewport(), Pair()])
        {
            var match = pattern.Match(decoded);
            if (match.Success && Read(match.Groups["lat"].Value, match.Groups["lng"].Value) is { } point)
            {
                return point;
            }
        }

        var dms = Degrees().Match(decoded);
        if (!dms.Success)
        {
            return null;
        }

        decimal Part(string degrees, string minutes, string seconds, string hemisphere)
        {
            var value = decimal.Parse(degrees, CultureInfo.InvariantCulture) +
                decimal.Parse(minutes, CultureInfo.InvariantCulture) / 60m +
                decimal.Parse(seconds, CultureInfo.InvariantCulture) / 3600m;

            return hemisphere is "S" or "W" ? -value : value;
        }

        var found = From(
            Part(dms.Groups["latD"].Value, dms.Groups["latM"].Value, dms.Groups["latS"].Value, dms.Groups["ns"].Value),
            Part(dms.Groups["lngD"].Value, dms.Groups["lngM"].Value, dms.Groups["lngS"].Value, dms.Groups["ew"].Value));

        return found.IsSuccess ? found.Value : null;
    }

    private static GeoPoint? Read(string latitude, string longitude)
    {
        if (!decimal.TryParse(latitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
            !decimal.TryParse(longitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lng))
        {
            return null;
        }

        var point = From(lat, lng);

        return point.IsSuccess ? point.Value : null;
    }

    // The pin of a place link: ...!3d23.8103!4d90.4125
    [GeneratedRegex(@"!3d(?<lat>-?\d{1,2}\.\d+)!4d(?<lng>-?\d{1,3}\.\d+)")]
    private static partial Regex PlacePin();

    // ?q=, query=, ll=, destination= or daddr= followed by the coordinates
    [GeneratedRegex(@"[?&](?:q|query|ll|sll|destination|daddr|center)=(?:loc:)?\s*(?<lat>-?\d{1,2}\.\d+)\s*,\s*(?<lng>-?\d{1,3}\.\d+)")]
    private static partial Regex Parameter();

    // The map's centre: /@23.8103,90.4125,17z
    [GeneratedRegex(@"@(?<lat>-?\d{1,2}\.\d+),(?<lng>-?\d{1,3}\.\d+)")]
    private static partial Regex Viewport();

    [GeneratedRegex(@"(?<![\d.])(?<lat>-?\d{1,2}\.\d+)\s*[,\s]\s*(?<lng>-?\d{1,3}\.\d+)(?![\d.])")]
    private static partial Regex Pair();

    [GeneratedRegex("""(?<latD>\d{1,2})°\s*(?<latM>\d{1,2})['′]\s*(?<latS>\d{1,2}(?:\.\d+)?)["″]?\s*(?<ns>[NS])[\s,]*(?<lngD>\d{1,3})°\s*(?<lngM>\d{1,2})['′]\s*(?<lngS>\d{1,2}(?:\.\d+)?)["″]?\s*(?<ew>[EW])""")]
    private static partial Regex Degrees();
}
