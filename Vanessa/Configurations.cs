using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kxnrl.Vanessa;

/// <summary>
///     Where the current lyric line is displayed. Discord only renders two text lines
///     (Details above State) and puts the progress bar underneath them, so a lyric can
///     only take one of those lines — or a button label below the bar.
/// </summary>
internal enum LyricsPlacement
{
    /// <summary>Details = "title · artist", State = lyric (default).</summary>
    ArtistInDetails = 0,

    /// <summary>Details = "title", State = lyric, artist moves to the large image tooltip.</summary>
    ArtistInTooltip = 1,

    /// <summary>Details/State unchanged, lyric shown on a second button below the progress bar.</summary>
    ButtonLabel = 2,
}

internal sealed class Configurations
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        WriteIndented               = true,
        Converters                  = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;

    /// <summary>True only when the config file did not exist yet (fresh install).</summary>
    public bool IsFirstLoad { get; }

    public bool ShowLyrics { get; set; } = true;

    public bool ShowTranslation { get; set; }

    public LyricsPlacement LyricsLayout { get; set; } = LyricsPlacement.ArtistInDetails;

    public Configurations()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vanessa");
        Directory.CreateDirectory(dir);

        _path       = Path.Combine(dir, "config.json");
        IsFirstLoad = !File.Exists(_path);

        if (IsFirstLoad)
        {
            Save();

            return;
        }

        try
        {
            var json = File.ReadAllText(_path, Encoding.UTF8);

            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            var file = JsonSerializer.Deserialize<ConfigFile>(json, JsonOptions);

            if (file is null)
            {
                return;
            }

            ShowLyrics      = file.ShowLyrics;
            ShowTranslation = file.ShowTranslation;
            LyricsLayout    = file.LyricsLayout;
        }
        catch (Exception e) when (e is IOException
                                       or JsonException
                                       or UnauthorizedAccessException
                                       or NotSupportedException)
        {
            // a broken config must never block startup: fall back to the defaults
        }
    }

    public void Save()
    {
        try
        {
            var file = new ConfigFile
            {
                ShowLyrics      = ShowLyrics,
                ShowTranslation = ShowTranslation,
                LyricsLayout    = LyricsLayout,
            };

            File.WriteAllText(_path, JsonSerializer.Serialize(file, JsonOptions), Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // persisting is best effort only
        }
    }
}

internal sealed class ConfigFile
{
    public bool ShowLyrics { get; set; } = true;

    public bool ShowTranslation { get; set; }

    public LyricsPlacement LyricsLayout { get; set; } = LyricsPlacement.ArtistInDetails;
}
