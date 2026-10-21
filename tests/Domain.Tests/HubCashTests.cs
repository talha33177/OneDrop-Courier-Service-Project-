using Domain.Delivery;
using Domain.Payments;
using Domain.Staff;

namespace Domain.Tests;

/// <summary>The cash a hub holds after closing its riders' runs: deposits finance confirms, and shortfalls the hub manager accepts.</summary>
public class HubCashTests
{
    private static readonly DateOnly Today = new(2026, 10, 21);
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_deposit_is_the_slip_as_the_bank_stamped_it_and_never_more_than_the_hub_holds()
    {
        static Domain.Common.Result<CashDeposit> Record(
            decimal amount = 5000,
            string? bank = "City Bank, Mirpur",
            string? slip = "ab-1234",
            int daysAgo = 0,
            decimal inHand = 8000)
        {
            return CashDeposit.Record(Build.Mirpur, amount, bank, slip, Today.AddDays(-daysAgo), inHand, Today, 42);
        }

        Assert.Equal("deposit.amount", Record(amount: 0).Error?.Code);
        Assert.Equal("deposit.amount", Record(amount: 10.005m).Error?.Code);
        Assert.Equal("deposit.tooMuch", Record(amount: 8001).Error?.Code);
        Assert.Equal("deposit.bank", Record(bank: " ").Error?.Code);
        Assert.Equal("deposit.slip", Record(slip: null).Error?.Code);
        Assert.Equal("deposit.day", Record(daysAgo: -1).Error?.Code);
        Assert.Equal("deposit.day", Record(daysAgo: CashDeposit.MaxDaysLate + 1).Error?.Code);

        var deposit = Record(amount: 8000, daysAgo: 2).Value;
        Assert.Equal(
            (8000m, "City Bank, Mirpur", "AB-1234", Today.AddDays(-2), CashDepositStatus.Recorded, 42L),
            (deposit.Amount, deposit.Bank, deposit.SlipNumber, deposit.DepositedOn, deposit.Status, deposit.RecordedById));
    }

    [Fact]
    public void Finance_confirms_a_deposit_or_refuses_it_with_what_the_bank_shows_once()
    {
        var confirmed = CashDeposit.Record(Build.Mirpur, 5000, "City Bank", "S-1", Today, 5000, Today, 42).Value;
        Assert.True(confirmed.Confirm(77, Now).IsSuccess);
        Assert.Equal((CashDepositStatus.Confirmed, 77L, Now), (confirmed.Status, confirmed.AnsweredById, confirmed.AnsweredOn));
        Assert.Equal("deposit.answered", confirmed.Refuse("Not there", 77, Now).Error?.Code);

        var refused = CashDeposit.Record(Build.Mirpur, 5000, "City Bank", "S-2", Today, 5000, Today, 42).Value;
        Assert.Equal("deposit.reason", refused.Refuse(" ", 77, Now).Error?.Code);
        Assert.True(refused.Refuse("Only ৳4,500 on the statement", 77, Now).IsSuccess);
        Assert.Equal((CashDepositStatus.Refused, "Only ৳4,500 on the statement"), (refused.Status, refused.Answer));
        Assert.Equal("deposit.answered", refused.Confirm(77, Now).Error?.Code);
    }

    [Fact]
    public void A_rider_short_of_cash_waits_for_the_hub_manager_who_accepts_it_once_and_says_what_was_done()
    {
        var rider = Build.Rider();
        var full = Build.WithId(DeliveryRun.Open(rider, Today), 9);
        full.Close(2000, 2000, Now);
        Assert.False(full.ShortWaiting);
        Assert.Equal("run.short.none", full.AcceptShort("Fine", 42, Now).Error?.Code);

        var open = Build.WithId(DeliveryRun.Open(rider, Today), 10);
        Assert.False(open.ShortWaiting);

        var shortRun = Build.WithId(DeliveryRun.Open(rider, Today), 11);
        shortRun.Close(2085, 2000, Now);
        Assert.True(shortRun.ShortWaiting);
        Assert.Equal("run.short.note", shortRun.AcceptShort("  ", 42, Now).Error?.Code);
        Assert.True(shortRun.AcceptShort(" Taken from Rafiq's pay ", 42, Now).IsSuccess);

        Assert.Equal((false, "Taken from Rafiq's pay", 42L, Now), (shortRun.ShortWaiting, shortRun.ShortNote, shortRun.ShortAcceptedById, shortRun.ShortAcceptedOn));
        Assert.Equal("This shortfall has already been accepted.", shortRun.AcceptShort("Again", 42, Now).Error?.Message);
    }

    [Fact]
    public void Only_managers_accept_a_shortfall_unless_it_is_ticked_for_someone()
    {
        Assert.True(StaffJob.HubManager.Preset().HasFlag(StaffPermissions.CashShortfalls));
        Assert.True(StaffJob.OperationsManager.Preset().HasFlag(StaffPermissions.CashShortfalls));
        Assert.False(StaffJob.CashDesk.Preset().HasFlag(StaffPermissions.CashShortfalls));
        Assert.False(StaffJob.Relief.Preset().HasFlag(StaffPermissions.CashShortfalls));

        // A hub job may be given it: it is hub work, not head office's
        Assert.True(StaffMember.Add(42, 7, StaffJob.CashDesk, StaffPermissions.CashShortfalls).Value.May(StaffPermissions.CashShortfalls));
    }
}
