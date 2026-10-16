using Microsoft.EntityFrameworkCore;
using Domain.Parcels;

namespace Integration.Tests;

/// <summary>
/// Parcels that stay in one place longer than the courier's days are listed for its admin, in the bell and in the menu.
/// A parcel's last move is set back in the database, because waiting days is not an option in a test.
/// </summary>
public class StuckParcelsTests(WebAppFactory factory) : AppTests(factory)
{
    // Older than any parcel a test leaves behind, so this one is among the first listed
    private static readonly DateTime LongAgo = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_parcel_that_has_not_moved_for_the_courier_s_days_is_stuck_and_a_note_does_not_move_it()
    {
        WebAppFactory.RequireDatabase();
        var shop = await NewMerchantAsync();
        var code = await BookAsync(shop.ApiKey, area: "Gulshan 2");
        var held = await BookAsync(shop.ApiKey, area: "Gulshan 2");
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");
        await admin.SubmitAsync("/Hub/Scan?hub=GUL", "/Hub/Scan?mode=Receive&hub=GUL", ("Code", code));
        await admin.SubmitAsync("/Hub/Scan?hub=GUL", "/Hub/Scan?mode=Receive&hub=GUL", ("Code", held));

        // Receiving it moved it: the save stamped the time
        Assert.True((DateTime.UtcNow - (await ParcelAsync(code)).MovedOn).Duration() < TimeSpan.FromMinutes(5));
        Assert.DoesNotContain(code, await admin.PageAsync("/Admin/Stuck?status=AtHub"));

        await SetAsync(code, parcels => parcels.ExecuteUpdateAsync(s => s.SetProperty(p => p.MovedOn, LongAgo), Cancel));
        var stuck = await admin.PageAsync("/Admin/Stuck?status=AtHub");
        Assert.Contains(code, stuck);
        Assert.Contains("Gulshan hub", stuck);
        Assert.Contains("stuck in one place for over 3 days", await admin.PageAsync("/Admin"));

        // A flag is a note in its history, not a move
        await admin.SubmitAsync($"/Hub/Parcel/{code}", $"/Hub/Parcel/{code}?handler=Flag", ("issue", "InReview"), ("note", "Label torn"));
        Assert.Equal(LongAgo, (await ParcelAsync(code)).MovedOn);
        Assert.Contains(code, await admin.PageAsync("/Admin/Stuck?status=AtHub"));

        // On hold for a day still to come is waiting, not stuck; long past that day it is stuck
        var today = Today;
        await SetAsync(held, parcels => parcels.ExecuteUpdateAsync(
            s => s.SetProperty(p => p.Status, ParcelStatus.OnHold).SetProperty(p => p.HoldUntil, today.AddDays(1)).SetProperty(p => p.MovedOn, LongAgo),
            Cancel));
        Assert.DoesNotContain(held, await admin.PageAsync("/Admin/Stuck?status=OnHold"));
        await SetAsync(held, parcels => parcels.ExecuteUpdateAsync(s => s.SetProperty(p => p.HoldUntil, today.AddDays(-10)), Cancel));
        Assert.Contains(held, await admin.PageAsync("/Admin/Stuck?status=OnHold"));

        // Another courier's admin never sees them
        var rival = await SignInAsync("rival", "admin@rival.test");
        var rivalPage = await rival.PageAsync("/Admin/Stuck");
        Assert.DoesNotContain(code, rivalPage);
        Assert.DoesNotContain(held, rivalPage);
    }

    private async Task SetAsync(string code, Func<IQueryable<Parcel>, Task<int>> change)
    {
        Assert.Equal(1, await QueryAsync("onedrop", db => change(db.Parcels.Where(p => p.TrackingCode == code))));
    }
}
