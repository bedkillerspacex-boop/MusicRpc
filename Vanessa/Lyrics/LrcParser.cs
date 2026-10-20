using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Kxnrl.Vanessa.Lyrics;

/// <summary>
///     Minimal LRC parser: time tags, multiple tags per line, metadata tags and [offset:].
///     Anything it cannot understand is dropped instead of throwing.
/// </summary>
internal static class LrcParser
{
    private static readonly Regex TimeTag = new(@"\[(\d+):(\d+)(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

    private static readonly Regex MetaTag = new(@"^\[(ti|ar|al|by|offset|length|re|ve|kana):(.*)\]$",
                                                RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // NetEase puts the credits ("作词 : 周杰伦", "Producer: ...") at 00:00-00:10 as normal
    // timed lines. They are not lyrics, so drop them instead of showing them as the song plays.
    private static readonly Regex CreditTag = new(
        @"^(作词|作曲|编曲|制作人|制作|和声编写|和声|吉他|贝斯|鼓|键盘|弦乐|录音工程|录音助理|录音师|录音|混音师|混音|母带工程|母带|出品|监制|统筹|策划|发行|词|曲|OP|SP|Producer|Composer|Arranger|Lyrics|Lyricist|Mixed|Mix|Mastering|Recorded|Recording|Guitar|Bass|Drums|Keyboard|Strings)\s*[:：]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // NetEase answers songs without lyrics with a single placeholder line ("暂无歌词").
    // Treat it as "no lyrics" so the presence falls back to the artist instead of
    // showing "🎶 暂无歌词" forever.
    private static readonly Regex Placeholder = new(@"暂无歌词|此歌曲为没有填词的纯音乐|^纯音乐[，,]\s*请欣赏",
                                                     RegexOptions.Compiled);

    // translations are matched to the original line by timestamp; a couple of milliseconds
    // of drift between the two files is normal, so allow a small tolerance.
    private const int TranslationToleranceMs = 300;

    public static LyricLine[] Parse(string? lrc, string? translation)
    {
        var timed = ParseTimedLines(lrc, out var offset);

        if (timed.Count == 0)
        {
            return Array.Empty<LyricLine>();
        }

        var translations = ParseTranslations(translation);

        timed.Sort(static (x, y) => x.Time.CompareTo(y.Time));

        var lines = new List<LyricLine>(timed.Count);

        foreach (var (rawTime, text) in timed)
        {
            var time = Math.Max(0, rawTime - offset);

            lines.Add(new LyricLine(time, text, Lookup(translations, Key(time)) ?? string.Empty));
        }

        return lines.ToArray();
    }

    private static List<(double Time, string Text)> ParseTimedLines(string? lrc, out double offset)
    {
        var timed = new List<(double, string)>();

        offset = 0;

        if (string.IsNullOrWhiteSpace(lrc))
        {
            return timed;
        }

        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0)
            {
                continue;
            }

            var meta = MetaTag.Match(line);

            if (meta.Success)
            {
                if (meta.Groups[1].Value.Equals("offset", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(meta.Groups[2].Value.Trim(),
                                       NumberStyles.Float,
                                       CultureInfo.InvariantCulture,
                                       out var parsed))
                {
                    offset = parsed / 1000.0;
                }

                continue;
            }

            var tags = TimeTag.Matches(line);

            if (tags.Count == 0)
            {
                continue;
            }

            // an empty text line is a gap: keep showing the previous line instead of
            // blanking the presence, so drop it here.
            var text = TimeTag.Replace(line, string.Empty).Trim();

            if (text.Length == 0 || CreditTag.IsMatch(text) || Placeholder.IsMatch(text))
            {
                continue;
            }

            foreach (Match tag in tags)
            {
                timed.Add((TimeOf(tag), text));
            }
        }

        return timed;
    }

    private static Dictionary<int, string> ParseTranslations(string? lrc)
    {
        var map = new Dictionary<int, string>();

        if (string.IsNullOrWhiteSpace(lrc))
        {
            return map;
        }

        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0 || MetaTag.IsMatch(line))
            {
                continue;
            }

            var tags = TimeTag.Matches(line);

            if (tags.Count == 0)
            {
                continue;
            }

            var text = TimeTag.Replace(line, string.Empty).Trim();

            if (text.Length == 0)
            {
                continue;
            }

            foreach (Match tag in tags)
            {
                map[Key(TimeOf(tag))] = text;
            }
        }

        return map;
    }

    private static double TimeOf(Match tag)
    {
        var minutes = int.Parse(tag.Groups[1].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(tag.Groups[2].Value, CultureInfo.InvariantCulture);

        return minutes * 60 + seconds + Fraction(tag.Groups[3].Value);
    }

    /// <summary>[mm:ss.xx] uses 1-3 fractional digits, all meaning a fraction of a second.</summary>
    private static double Fraction(string digits)
    {
        if (digits.Length == 0)
        {
            return 0;
        }

        var value = int.Parse(digits, CultureInfo.InvariantCulture);

        return digits.Length switch
        {
            1 => value / 10.0,
            2 => value / 100.0,
            _ => value / 1000.0,
        };
    }

    private static int Key(double seconds)
        => (int) Math.Round(seconds * 1000);

    private static string? Lookup(Dictionary<int, string> map, int key)
    {
        if (map.Count == 0)
        {
            return null;
        }

        if (map.TryGetValue(key, out var exact))
        {
            return exact;
        }

        var    bestDistance = int.MaxValue;
        string? best        = null;

        foreach (var (otherKey, value) in map)
        {
            var distance = Math.Abs(otherKey - key);

            if (distance >= bestDistance || distance > TranslationToleranceMs)
            {
                continue;
            }

            bestDistance = distance;
            best         = value;
        }

        return best;
    }
}
