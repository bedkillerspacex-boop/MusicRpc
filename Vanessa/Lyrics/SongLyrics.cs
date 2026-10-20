using System;

namespace Kxnrl.Vanessa.Lyrics;

/// <summary>
///     A single lyric line: the timestamp (seconds) plus the original text and its translation.
/// </summary>
internal readonly record struct LyricLine(double Time, string Text, string Translation);

/// <summary>
///     Immutable lyrics of one song, safe to share between the background fetch and the polling loop.
/// </summary>
internal sealed class SongLyrics
{
    private readonly LyricLine[] _lines;

    public SongLyrics(LyricLine[] lines)
        => _lines = lines;

    public bool IsEmpty
        => _lines.Length == 0;

    /// <summary>
    ///     Index of the last line whose timestamp is &lt;= <paramref name="seconds" />, or -1 before the first line.
    /// </summary>
    public int IndexOf(double seconds)
    {
        var low    = 0;
        var high   = _lines.Length - 1;
        var result = -1;

        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);

            if (_lines[middle].Time <= seconds)
            {
                result = middle;
                low    = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return result;
    }

    public string TextAt(int index)
        => index >= 0 && index < _lines.Length ? _lines[index].Text : string.Empty;

    public string TranslationAt(int index)
        => index >= 0 && index < _lines.Length ? _lines[index].Translation : string.Empty;
}
