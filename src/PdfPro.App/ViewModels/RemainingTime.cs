using System.Globalization;
using PdfPro.App.Resources;

namespace PdfPro.App.ViewModels;

/// <summary>How long a task still needs, from how long its done part took.</summary>
internal static class RemainingTime
{
    /// <summary>Too little is done before this share for an estimate worth showing.</summary>
    public const double MinShare = 0.05;

    /// <summary>The time left when <paramref name="share"/> (0–1) took <paramref name="elapsed"/>; null before <see cref="MinShare"/> is done.</summary>
    public static TimeSpan? Estimate(TimeSpan elapsed, double share)
        => share < MinShare || share >= 1 || elapsed <= TimeSpan.Zero ? null : elapsed * ((1 - share) / share);

    /// <summary>"còn khoảng 40 giây", "còn khoảng 3 phút", "còn khoảng 1 giờ 10 phút".</summary>
    public static string Describe(TimeSpan left)
    {
        var culture = CultureInfo.CurrentCulture;
        if (left < TimeSpan.FromSeconds(55))
        {
            var seconds = Math.Max(5, (int)Math.Ceiling(left.TotalSeconds / 5) * 5);
            return string.Format(culture, Strings.TimeLeft_Seconds, seconds);
        }

        if (left < TimeSpan.FromMinutes(59.5))
        {
            return string.Format(culture, Strings.TimeLeft_Minutes, Math.Max(1, (int)Math.Round(left.TotalMinutes, MidpointRounding.AwayFromZero)));
        }

        var minutes = (int)Math.Round(left.TotalMinutes, MidpointRounding.AwayFromZero);
        return string.Format(culture, Strings.TimeLeft_Hours, minutes / 60, minutes % 60);
    }
}
