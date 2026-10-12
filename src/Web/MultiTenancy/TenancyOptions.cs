namespace Web.MultiTenancy;

public class TenancyOptions
{
    /// <summary>
    /// The host tenants are subdomains of: the production domain, "localhost" in development
    /// (browsers resolve onedrop.localhost to this machine). The bare root domain is the platform portal.
    /// </summary>
    public string RootDomain { get; set; } = "localhost";

    /// <summary>Subdomains that never name a tenant.</summary>
    public string[] ReservedSubdomains { get; set; } = ["www", "api", "admin"];

    /// <summary>
    /// The courier whose site a visitor to the bare root domain is sent to, by its slug; null or unknown shows the list
    /// of couriers instead. Platform staff still sign in on the root domain at <c>/Account/Login</c>.
    /// </summary>
    public string? HomeCourier { get; set; }

    /// <summary>
    /// One address that serves <see cref="HomeCourier"/> by itself (a hosting platform's own address, or any name such as
    /// courier.example.com), for a live site where couriers are not subdomains of <see cref="RootDomain"/>. Empty: the
    /// home courier is {slug}.{RootDomain} like any other.
    /// </summary>
    public string? HomeCourierHost { get; set; }

    /// <summary>The tenant slug in a host name, or null for the root domain and anything unrelated.</summary>
    public string? SlugFromHost(string host)
    {
        if (HomeCourierHost is { Length: > 0 } &&
            HomeCourier is { Length: > 0 } &&
            string.Equals(host, HomeCourierHost, StringComparison.OrdinalIgnoreCase))
        {
            return HomeCourier;
        }

        var suffix = "." + RootDomain;
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var subdomain = host[..^suffix.Length];

        return subdomain.Length == 0 ||
            subdomain.Contains('.') ||
            ReservedSubdomains.Contains(subdomain, StringComparer.OrdinalIgnoreCase)
            ? null
            : subdomain.ToLowerInvariant();
    }

    /// <summary>The portal address of a tenant, keeping the current scheme and port.</summary>
    public string TenantUrl(HttpRequest request, string slug)
    {
        var port = request.Host.Port.HasValue ? $":{request.Host.Port}" : "";

        return HomeCourierHost is { Length: > 0 } && string.Equals(slug, HomeCourier, StringComparison.OrdinalIgnoreCase)
            ? $"{request.Scheme}://{HomeCourierHost}{port}/"
            : $"{request.Scheme}://{slug}.{RootDomain}{port}/";
    }

    public string PlatformUrl(HttpRequest request)
    {
        var port = request.Host.Port.HasValue ? $":{request.Host.Port}" : "";

        return $"{request.Scheme}://{RootDomain}{port}/";
    }
}
