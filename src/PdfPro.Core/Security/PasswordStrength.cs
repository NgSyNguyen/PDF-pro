namespace PdfPro.Core.Security;

/// <summary>How hard a password is to guess.</summary>
public enum PasswordLevel
{
    VeryWeak = 0,
    Weak = 1,
    Fair = 2,
    Strong = 3,
    VeryStrong = 4,
}

/// <summary>Why a password is weak (each turned into advice in the user's language).</summary>
[Flags]
public enum PasswordWeakness
{
    None = 0,
    TooShort = 1 << 0,
    OnlyDigits = 1 << 1,
    OnlyLetters = 1 << 2,
    Common = 1 << 3,
    Repeated = 1 << 4,
    Sequence = 1 << 5,
    LooksLikeDate = 1 << 6,
}

/// <summary>A password's level, an estimate of its strength in bits, and why it is weak.</summary>
public sealed record PasswordAssessment(PasswordLevel Level, double Bits, PasswordWeakness Weaknesses)
{
    public bool IsWeak => Level <= PasswordLevel.Weak;
}

/// <summary>
/// Estimates how hard a password is to guess, offline: the size of the alphabet it draws from times its length, less
/// for repeats, sequences ("1234", "abcd", "qwerty"), dates and well-known passwords (also Vietnamese ones such as
/// "matkhau"). Not a promise — advice for the user.
/// </summary>
public static class PasswordStrength
{
    private const int MinLength = 8;

    private static readonly HashSet<string> CommonPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "123456", "1234567", "12345678", "123456789", "1234567890", "111111", "000000", "123123", "654321", "666666", "888888",
        "password", "password1", "passw0rd", "qwerty", "qwerty123", "abc123", "abcd1234", "iloveyou", "admin", "admin123",
        "letmein", "welcome", "monkey", "dragon", "football", "baseball", "sunshine", "princess", "master", "secret",
        "matkhau", "matkhau123", "anhyeuem", "emyeuanh", "yeuem", "vietnam", "hanoi", "saigon", "123456a", "a123456",
    };

    private static readonly string[] Rows = ["qwertyuiop", "asdfghjkl", "zxcvbnm", "1234567890", "abcdefghijklmnopqrstuvwxyz"];

    public static PasswordAssessment Assess(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return new PasswordAssessment(PasswordLevel.VeryWeak, 0, PasswordWeakness.TooShort);
        }

        var weaknesses = PasswordWeakness.None;
        var alphabet = 0;
        alphabet += password.Any(char.IsAsciiLetterLower) ? 26 : 0;
        alphabet += password.Any(char.IsAsciiLetterUpper) ? 26 : 0;
        alphabet += password.Any(char.IsAsciiDigit) ? 10 : 0;
        alphabet += password.Any(c => c < 128 && !char.IsAsciiLetterOrDigit(c)) ? 33 : 0;
        alphabet += password.Any(c => c >= 128) ? 100 : 0; // Vietnamese letters and others
        var distinct = password.Distinct().Count();
        var bits = Math.Log2(Math.Max(alphabet, 2)) * password.Length;

        if (password.Length < MinLength)
        {
            weaknesses |= PasswordWeakness.TooShort;
        }

        if (password.All(char.IsAsciiDigit))
        {
            weaknesses |= PasswordWeakness.OnlyDigits;
        }
        else if (password.All(char.IsLetter))
        {
            weaknesses |= PasswordWeakness.OnlyLetters;
        }

        if (CommonPasswords.Contains(password) || CommonPasswords.Contains(password.TrimEnd('!', '.', '@', '#')))
        {
            weaknesses |= PasswordWeakness.Common;
            bits = Math.Min(bits, 10);
        }

        if (distinct <= Math.Max(2, password.Length / 3))
        {
            weaknesses |= PasswordWeakness.Repeated;
            bits = Math.Min(bits, Math.Log2(Math.Max(alphabet, 2)) * distinct * 1.5);
        }

        if (HasSequence(password))
        {
            weaknesses |= PasswordWeakness.Sequence;
            bits *= 0.6;
        }

        if (LooksLikeDate(password))
        {
            weaknesses |= PasswordWeakness.LooksLikeDate;
            bits = Math.Min(bits, 16);
        }

        var level = bits switch
        {
            < 28 => PasswordLevel.VeryWeak,
            < 40 => PasswordLevel.Weak,
            < 60 => PasswordLevel.Fair,
            < 80 => PasswordLevel.Strong,
            _ => PasswordLevel.VeryStrong,
        };
        if (weaknesses.HasFlag(PasswordWeakness.TooShort) && level > PasswordLevel.Weak)
        {
            level = PasswordLevel.Weak;
        }

        return new PasswordAssessment(level, Math.Round(bits, 1), weaknesses);
    }

    /// <summary>Four or more keys in a row of the keyboard or of the alphabet, forwards or backwards.</summary>
    private static bool HasSequence(string password)
    {
        var lower = password.ToLowerInvariant();
        for (var i = 0; i + 4 <= lower.Length; i++)
        {
            var piece = lower.Substring(i, 4);
            var backwards = new string([.. piece.Reverse()]);
            if (Rows.Any(row => row.Contains(piece, StringComparison.Ordinal) || row.Contains(backwards, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Digits only (or with / . -) forming a date such as 27101990, 1990-10-27, 27/10/90.</summary>
    private static bool LooksLikeDate(string password)
    {
        var digits = new string([.. password.Where(char.IsAsciiDigit)]);
        if (digits.Length is not (6 or 8) || password.Any(c => !char.IsAsciiDigit(c) && c is not ('/' or '.' or '-')))
        {
            return false;
        }

        bool Day(string d) => int.Parse(d, System.Globalization.CultureInfo.InvariantCulture) is >= 1 and <= 31;
        bool Month(string m) => int.Parse(m, System.Globalization.CultureInfo.InvariantCulture) is >= 1 and <= 12;
        bool Year(string y) => y.Length == 2 || int.Parse(y, System.Globalization.CultureInfo.InvariantCulture) is >= 1900 and <= 2100;
        return digits.Length == 8
            ? (Day(digits[..2]) && Month(digits[2..4]) && Year(digits[4..])) || (Year(digits[..4]) && Month(digits[4..6]) && Day(digits[6..]))
            : Day(digits[..2]) && Month(digits[2..4]) && Year(digits[4..]);
    }
}
