using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Kxnrl.Vanessa.Lyrics;

/// <summary>
///     Downloads lyrics from the official NetEase Cloud Music endpoint:
///     https://music.163.com/api/song/lyric
///     Every failure path returns null — the caller falls back to showing the artist,
///     so a dead network can never break the presence itself.
/// </summary>
internal static class NetEaseLyrics
{
    private const string Endpoint = "https://music.163.com/api/song/lyric?os=pc&id={0}&lv=-1&kv=-1&tv=-1";

    private const int CacheCapacity = 16;

    private static readonly HttpClient Client = CreateClient();

    private static readonly Dictionary<string, SongLyrics> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string>                  CacheOrder = new();

    public static async Task<SongLyrics?> FetchAsync(string songId, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(songId))
        {
            return null;
        }

        lock (Cache)
        {
            if (Cache.TryGetValue(songId, out var cached))
            {
                return cached;
            }
        }

        try
        {
            var url = string.Format(CultureInfo.InvariantCulture, Endpoint, songId);

            await using var stream = await Client.GetStreamAsync(url, token).ConfigureAwait(false);

            var payload = await JsonSerializer
                                .DeserializeAsync<LyricResponse>(stream, cancellationToken: token)
                                .ConfigureAwait(false);

            var original = payload?.Lrc?.Lyric;

            if (string.IsNullOrWhiteSpace(original))
            {
                return null;
            }

            var lines = LrcParser.Parse(original, payload?.Translated?.Lyric);

            if (lines.Length == 0)
            {
                return null;
            }

            var lyrics = new SongLyrics(lines);

            Remember(songId, lyrics);

            return lyrics;
        }
        catch (Exception e) when (e is HttpRequestException
                                       or TaskCanceledException
                                       or JsonException
                                       or NotSupportedException
                                       or InvalidOperationException)
        {
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        // the api is domestic: bypass any system proxy (Clash / corporate) that would
        // either slow the request down or answer with a 403.
        var handler = new HttpClientHandler
        {
            UseProxy             = false,
            AutomaticDecompression = DecompressionMethods.All,
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");

        return client;
    }

    private static void Remember(string songId, SongLyrics lyrics)
    {
        lock (Cache)
        {
            if (Cache.ContainsKey(songId))
            {
                return;
            }

            Cache[songId] = lyrics;
            CacheOrder.Enqueue(songId);

            while (CacheOrder.Count > CacheCapacity)
            {
                Cache.Remove(CacheOrder.Dequeue());
            }
        }
    }

}

file sealed record LyricBody([property: JsonPropertyName("lyric")] string? Lyric);

file sealed record LyricResponse(
    [property: JsonPropertyName("lrc")]    LyricBody? Lrc,
    [property: JsonPropertyName("tlyric")] LyricBody? Translated);
