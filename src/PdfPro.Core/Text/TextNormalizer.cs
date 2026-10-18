using System.Text;

namespace PdfPro.Core.Text;

/// <summary>
/// Unicode clean-up for text read from PDF files (AGENTS rule 7: NFC for display, search, comparison and copy).
/// PDF strings are not validated by their producers, so these helpers never throw on malformed input.
/// </summary>
public static class TextNormalizer
{
    private const char ReplacementCharacter = '�';
    private const char ByteOrderMark = '﻿';

    /// <summary>
    /// The NFC form of <paramref name="text"/> (Vietnamese letters with combining marks become single characters).
    /// Unpaired surrogates, which are invalid UTF-16 and make normalization fail, become U+FFFD first.
    /// </summary>
    public static string ToNfc(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var valid = ReplaceUnpairedSurrogates(text);
        try
        {
            return valid.IsNormalized(NormalizationForm.FormC) ? valid : valid.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return valid;
        }
    }

    /// <summary>
    /// A one-line label (bookmark titles, document properties): line breaks, tabs and other control characters
    /// become spaces, runs of white space collapse to one space, byte order marks are dropped, the ends are trimmed
    /// and the result is NFC.
    /// </summary>
    public static string ToSingleLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (c == ByteOrderMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return ToNfc(builder.ToString());
    }

    private static string ReplaceUnpairedSurrogates(string text)
    {
        char[]? fixedChars = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                fixedChars ??= text.ToCharArray();
                fixedChars[i] = ReplacementCharacter;
            }
        }

        return fixedChars is null ? text : new string(fixedChars);
    }
}
