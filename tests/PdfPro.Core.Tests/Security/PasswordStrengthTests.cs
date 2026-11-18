using PdfPro.Core.Security;

namespace PdfPro.Core.Tests.Security;

public sealed class PasswordStrengthTests
{
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("123456")]
    [InlineData("matkhau")]
    [InlineData("Matkhau123")]
    [InlineData("password!")]
    [InlineData("aaaaaaaaaaaa")]
    [InlineData("27101990")]
    [InlineData("1990-10-27")]
    [InlineData("qwertyui")]
    [InlineData("abc")]
    public void WeakPasswords_AreWeak(string? password)
        => PasswordStrength.Assess(password).IsWeak.ShouldBeTrue();

    [Theory]
    [InlineData("Tr4ng-Sông#Hồng-2026", PasswordLevel.VeryStrong)]
    [InlineData("ngựa-xanh-đá-cuội-97", PasswordLevel.VeryStrong)]
    [InlineData("G7#vq9!Lm2", PasswordLevel.Strong)]
    public void LongMixedPasswords_AreStrong(string password, PasswordLevel atLeast)
    {
        var assessment = PasswordStrength.Assess(password);

        ((int)assessment.Level).ShouldBeGreaterThanOrEqualTo((int)atLeast);
        assessment.IsWeak.ShouldBeFalse();
    }

    [Fact]
    public void Weaknesses_SayWhy()
    {
        PasswordStrength.Assess("12345678").Weaknesses.ShouldBe(PasswordWeakness.OnlyDigits | PasswordWeakness.Common | PasswordWeakness.Sequence);
        PasswordStrength.Assess("abc").Weaknesses.HasFlag(PasswordWeakness.TooShort).ShouldBeTrue();
        PasswordStrength.Assess("hoahongdo").Weaknesses.ShouldBe(PasswordWeakness.OnlyLetters);
        PasswordStrength.Assess("27/10/1990").Weaknesses.HasFlag(PasswordWeakness.LooksLikeDate).ShouldBeTrue();
        PasswordStrength.Assess("xyz-zzzzzzzzzz").Weaknesses.HasFlag(PasswordWeakness.Repeated).ShouldBeTrue();
        PasswordStrength.Assess("Tr4ng-Sông#Hồng-2026").Weaknesses.ShouldBe(PasswordWeakness.None);
    }

    [Fact]
    public void AShortPassword_IsNeverMoreThanWeak()
        => PasswordStrength.Assess("Ü#7qZ!").Level.ShouldBe(PasswordLevel.Weak);

    [Fact]
    public void LongerPasswords_ScoreMoreBits()
        => PasswordStrength.Assess("G7#vq9!Lm2xR").Bits.ShouldBeGreaterThan(PasswordStrength.Assess("G7#vq9!Lm2").Bits);
}
