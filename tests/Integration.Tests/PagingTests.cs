using System.Text.RegularExpressions;

namespace Integration.Tests;

/// <summary>
/// The long lists page through their rows. Razor Pages names a page by the route value "page", so a query value of that
/// name never reached the list and every "Next" showed the first page again; the pager uses "p".
/// </summary>
public partial class PagingTests(WebAppFactory factory) : AppTests(factory)
{
    [Fact]
    public async Task Next_on_a_long_list_opens_its_next_page()
    {
        WebAppFactory.RequireDatabase();
        var admin = await SignInAsync("onedrop", "admin@onedrop.test");

        foreach (var list in new[] { "/Admin/Merchants", "/Hub/Parcels", "/Admin/Payouts" })
        {
            var first = await admin.PageAsync(list);
            var shown = PageOf().Match(first);
            if (!shown.Success || int.Parse(shown.Groups[2].Value) < 2)
            {
                continue;
            }

            Assert.Contains("p=2", first);
            Assert.Equal("2", PageOf().Match(await admin.PageAsync($"{list}?p=2")).Groups[1].Value);
        }
    }

    [GeneratedRegex(@"page (\d+) of (\d+)")]
    private static partial Regex PageOf();
}
