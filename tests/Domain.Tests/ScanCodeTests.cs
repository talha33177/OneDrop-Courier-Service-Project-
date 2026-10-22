using Domain.Parcels;

namespace Domain.Tests;

/// <summary>What a hand scanner or a phone's camera sends is read as the code on the label.</summary>
public class ScanCodeTests
{
    [Theory]
    [InlineData("OD10000001", "OD10000001")]
    [InlineData("  od10000001 \r\n", "OD10000001")]
    [InlineData("OD1000\t0001\n", "OD10000001")]
    [InlineData("]Q1OD10000001", "OD10000001")]
    [InlineData("]C0bg-100007", "BG-100007")]
    [InlineData("https://onedrop.example/Track?code=OD10000001", "OD10000001")]
    [InlineData("https://onedrop.example/Track?x=1&Code=od10000001", "OD10000001")]
    [InlineData("https://onedrop.example/Track/OD10000001/", "OD10000001")]
    public void A_scan_is_read_as_the_code_on_the_label(string scanned, string code)
    {
        Assert.Equal(code, ScanCode.Read(scanned));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n")]
    public void Nothing_scanned_is_nothing(string? scanned)
    {
        Assert.Null(ScanCode.Read(scanned));
    }

    [Fact]
    public void A_long_scan_is_not_a_code()
    {
        Assert.Null(ScanCode.Read(new string('A', 500)));
    }
}
