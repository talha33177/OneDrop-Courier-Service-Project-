namespace Application.Pricing.DeliveryOptions;

/// <summary>
/// What a parcel's speed and contents show in lists, labels, hub screens and the rider's app: each by its name and
/// icon, or null when it is the courier's default (a regular parcel of general goods shows no tags), how to handle
/// what is inside, and whether it goes first. Read from the options as they are now, so a renamed option shows its new
/// name; the charges stay as booked.
/// </summary>
public sealed record DeliveryTags(
    string? Speed,
    string SpeedIcon,
    string? Contents,
    string ContentsIcon,
    string? Handling,
    bool GoesFirst)
{
    public bool Any => Speed is not null || Contents is not null;
}
