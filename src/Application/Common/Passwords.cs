using System.Security.Cryptography;

namespace Application.Common;

/// <summary>First passwords for people added by someone else (moderators, staff), shown once to hand over.</summary>
public static class Passwords
{
    private const string Capitals = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Letters = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";

    /// <summary>
    /// Letters a person can read out, with two digits in the middle so it always passes the sign-in rules whatever
    /// the random draw.
    /// </summary>
    public static string New()
    {
        return RandomNumberGenerator.GetString(Capitals, 2)
            + RandomNumberGenerator.GetString(Letters, 4)
            + RandomNumberGenerator.GetString(Digits, 2)
            + RandomNumberGenerator.GetString(Letters, 4);
    }
}
