using Domain.Merchants;

namespace Domain.Tests;

/// <summary>The code a shop signing up is emailed: right once, within ten minutes and five tries.</summary>
public class SignUpCodeTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_right_code_works_once_within_its_time()
    {
        var code = SignUpCode.Issue("  Shop@Example.com ", "right", Now);
        Assert.Equal("shop@example.com", code.Email);

        Assert.True(code.Check("right", Now.AddMinutes(9)).IsSuccess);
        Assert.Equal("signUpCode.used", code.Check("right", Now.AddMinutes(9)).Error?.Code);
        Assert.Equal("signUpCode.expired", SignUpCode.Issue("a@b.c", "right", Now).Check("right", Now.AddMinutes(11)).Error?.Code);
    }

    [Fact]
    public void Five_wrong_codes_end_it_and_each_says_how_many_tries_are_left()
    {
        var code = SignUpCode.Issue("shop@example.com", "right", Now);

        Assert.Contains("4 tries left", code.Check("wrong", Now).Error!.Message);
        for (var attempt = 2; attempt <= SignUpCode.MaxAttempts; attempt++)
        {
            Assert.Equal("signUpCode.wrong", code.Check("wrong", Now).Error?.Code);
        }

        Assert.Equal("signUpCode.attempts", code.Check("right", Now).Error?.Code);
    }

    [Fact]
    public void A_new_code_may_be_asked_for_a_minute_after_the_last()
    {
        var code = SignUpCode.Issue("shop@example.com", "right", Now);

        Assert.False(code.AllowsResend(Now.AddSeconds(30)));
        Assert.True(code.AllowsResend(Now.AddMinutes(1)));
    }
}
