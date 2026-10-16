namespace Domain.Network;

/// <summary>Bangladesh's eight administrative divisions, the first step of the address picker (division, zone, area).</summary>
public static class Divisions
{
    public static readonly IReadOnlyList<string> All =
        ["Dhaka", "Chattogram", "Rajshahi", "Khulna", "Barishal", "Sylhet", "Rangpur", "Mymensingh"];

    /// <summary>The division's own spelling for a name typed in any case, or null when it is not one of the eight.</summary>
    public static string? Find(string? name)
    {
        return All.FirstOrDefault(division => string.Equals(division, name?.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
