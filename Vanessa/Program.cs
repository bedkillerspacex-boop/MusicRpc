using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DiscordRPC;
using DiscordRPC.Helper;
using Kxnrl.Vanessa.Lyrics;
using Kxnrl.Vanessa.Models;
using Kxnrl.Vanessa.Players;
using Button = DiscordRPC.Button;

namespace Kxnrl.Vanessa;

internal class Program
{
    // The name Discord shows ("Listening to ...") comes from the application registered
    // for this id, not from anything we send. Keep the upstream ids as defaults; a custom
    // id lets you show your own application name.
    private const string DefaultNetEaseAppId = "481562643958595594";
    private const string DefaultTencentAppId = "903485504899665990";

    // Discord RPC limits: Details/State/LargeImageText max 128 UTF-8 bytes, button label
    // max 32, image keys max 256 (see DiscordRPC.RichPresence)
    private const int MaxRpcTextBytes     = 128;
    private const int MaxRpcButtonBytes   = 32;
    private const int MaxRpcImageKeyBytes = 256;

    // Issue #44: state-driven presence updates.
    // Transmit immediately when the song / pause state / timeline actually
    // changes, plus a periodic resync as a safety net, instead of
    // unconditionally every loop (which defeated the library's
    // SkipIdenticalPresence via jittering UtcNow-based timestamps and flooded
    // the Discord client, which only picks up refreshes every ~2-3s).
    private const int    ClearDebounceLoops   = 6;    // ~1.4s at 233ms/loop: absorbs transient nulls while the player rewrites playingList on song switch
    private const double SeekThresholdSeconds = 3.0;  // playback position drift beyond this means the user sought / track wrapped / long rebuffer
    private const int    ResyncIntervalMs     = 5000; // periodic resync while nothing changed: self-heals dropped updates and accumulated drift

    // Discord accepts roughly 5 presence updates per 20 seconds. Song / pause / seek
    // changes still go out immediately; lyric advances queue up behind this interval
    // and are flushed by a later loop (the presence stays dirty until then).
    private const int MinSendIntervalMs = 4000;

    private const int LyricRetryDelayMs = 30000;

    private static async Task Main()
    {
        // check run once
        _ = new Mutex(true, "MusicDiscordRpc", out var allow);

        if (!allow)
        {
            MessageBox.Show("MusicDiscordRpc is already running.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

            Environment.Exit(-1);

            return;
        }

        var config = Constants.GlobalConfig;

        if (config.IsFirstLoad)
        {
            Win32Api.AutoStart.Set(true);
        }

        var netEaseAppId = Pick(config.NetEaseAppId, DefaultNetEaseAppId);
        var tencentAppId = Pick(config.TencentAppId, DefaultTencentAppId);

        var netEase = new DiscordRpcClient(netEaseAppId);
        var tencent = new DiscordRpcClient(tencentAppId);
        netEase.Initialize();
        tencent.Initialize();

        if (!netEase.IsInitialized || !tencent.IsInitialized)
        {
            MessageBox.Show("Failed to init rpc client.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.Exit(-1);
        }

        // TODO Online Signatures
        await Task.CompletedTask;

        var notifyMenu = new ContextMenuStrip();

        var exitButton = new ToolStripMenuItem("Exit");
        var autoButton = new ToolStripMenuItem("AutoStart" + "    " + (Win32Api.AutoStart.Check() ? "√" : "✘"));

        var lyricsButton      = new ToolStripMenuItem();
        var translationButton = new ToolStripMenuItem();
        var layoutMenu        = new ToolStripMenuItem("Lyrics Layout");

        var layoutLabels = new[]
        {
            "2nd line (artist on album line)",
            "2nd line (artist in 1st line)",
            "in button label",
        };

        // keep the menu order stable but map it onto the enum explicitly: the album-line
        // layout is the default, so it is listed first.
        var layoutValues = new[]
        {
            LyricsPlacement.ArtistInTooltip,
            LyricsPlacement.ArtistInDetails,
            LyricsPlacement.ButtonLabel,
        };

        var layoutItems = new ToolStripMenuItem[layoutLabels.Length];

        for (var i = 0; i < layoutItems.Length; i++)
        {
            var index = i;
            var item  = new ToolStripMenuItem(layoutLabels[index]);

            item.Click += (_, _) =>
            {
                config.LyricsLayout = layoutValues[index];
                config.Save();
                RefreshLyricsMenu();
            };

            layoutItems[index] = item;
            layoutMenu.DropDownItems.Add(item);
        }

        lyricsButton.Click += (_, _) =>
        {
            config.ShowLyrics = !config.ShowLyrics;
            config.Save();
            RefreshLyricsMenu();
        };

        translationButton.Click += (_, _) =>
        {
            config.ShowTranslation = !config.ShowTranslation;
            config.Save();
            RefreshLyricsMenu();
        };

        RefreshLyricsMenu();

        notifyMenu.Items.Add(autoButton);
        notifyMenu.Items.Add(new ToolStripSeparator());
        notifyMenu.Items.Add(lyricsButton);
        notifyMenu.Items.Add(translationButton);
        notifyMenu.Items.Add(layoutMenu);
        notifyMenu.Items.Add(new ToolStripSeparator());
        notifyMenu.Items.Add(exitButton);

        var notifyIcon = new NotifyIcon()
        {
            BalloonTipIcon   = ToolTipIcon.Info,
            ContextMenuStrip = notifyMenu,
            Text             = "NetEase Cloud Music DiscordRPC",
            Icon             = AppResource.icon,
            Visible          = true,
        };

        exitButton.Click += (_, _) =>
        {
            notifyIcon.Visible = false;
            Thread.Sleep(100);
            Environment.Exit(0);
        };

        autoButton.Click += (_, _) =>
        {
            var x = Win32Api.AutoStart.Check();

            Win32Api.AutoStart.Set(!x);

            autoButton.Text = "AutoStart" + "    " + (Win32Api.AutoStart.Check() ? "√" : "✘");
        };

        _ = Task.Run(async () => await UpdateThread(netEase, tencent));
        Application.Run();

        void RefreshLyricsMenu()
        {
            lyricsButton.Text      = "Show Lyrics" + "    " + (config.ShowLyrics ? "√" : "✘");
            translationButton.Text = "Show Translation" + "    " + (config.ShowTranslation ? "√" : "✘");

            translationButton.Enabled = config.ShowLyrics;

            for (var i = 0; i < layoutItems.Length; i++)
            {
                layoutItems[i].Text    = layoutLabels[i] + "    " + (layoutValues[i] == config.LyricsLayout ? "√" : "✘");
                layoutItems[i].Enabled = config.ShowLyrics;
            }
        }
    }

    private static async Task UpdateThread(DiscordRpcClient netEase, DiscordRpcClient tencent)
    {
        IMusicPlayer?     lastInstance  = null;
        DiscordRpcClient? lastRpcClient = null;

        // Snapshot of the presence Discord is currently displaying.
        // sentIdentity == null means nothing is displayed (cleared or never set).
        string? sentIdentity = null;
        double  sentSchedule = 0;
        double  sentDuration = 0;
        var     sentLyric    = -1;
        long    lastSendTick = Environment.TickCount64 - ResyncIntervalMs;
        var     nullStreak   = 0;

        var lyricState = new LyricState();
        var config     = Constants.GlobalConfig;

        while (true)
        {
            try
            {
                IMusicPlayer?     player    = null;
                DiscordRpcClient? rpcClient = null;
                var               isNetEase = false;

                if (Win32Api.User32.GetWindowTitle("OrpheusBrowserHost", out _, out var netEaseProcessId))
                {
                    player = lastInstance is null
                        ? new NetEase(netEaseProcessId)
                        : lastInstance.Validate(netEaseProcessId)
                            ? lastInstance
                            : new NetEase(netEaseProcessId);

                    rpcClient = netEase;
                    isNetEase = true;
                }
                else if (Win32Api.User32.GetWindowTitle("QQMusic_Daemon_Wnd", out _, out var tencentId))
                {
                    player = lastInstance is null
                        ? new Tencent(tencentId)
                        : lastInstance.Validate(tencentId)
                            ? lastInstance
                            : new Tencent(tencentId);

                    rpcClient = tencent;
                }

                lastInstance = player;

                // Player switched (NetEase <-> QQMusic): the old presence lives on a
                // different client (different AppId / pipe) and would linger forever.
                // Clear it, then reset tracking so the new player sends immediately.
                if (lastRpcClient is not null && rpcClient is not null && !ReferenceEquals(rpcClient, lastRpcClient))
                {
                    lastRpcClient.ClearPresence();

                    sentIdentity  = null;
                    sentLyric     = -1;
                    lastRpcClient = null;
                }

                var pi = player?.GetPlayerInfo();

                Debug.Print(pi is not null ? JsonSerializer.Serialize(pi) : "null");

                if (pi is not { } info || rpcClient is null)
                {
                    // Song switches produce transient nulls while the player rewrites
                    // playingList asynchronously; only clear after the signal has been
                    // gone for several consecutive loops (player exited / stopped),
                    // never on a single glitch.
                    nullStreak = Math.Min(nullStreak + 1, ClearDebounceLoops);

                    if (nullStreak >= ClearDebounceLoops && sentIdentity is not null)
                    {
                        lastRpcClient?.ClearPresence();

                        sentIdentity  = null;
                        sentLyric     = -1;
                        lastRpcClient = null;
                    }

                    continue;
                }

                nullStreak = 0;

                var now = Environment.TickCount64;

                // Lyrics: reset on song change, fetch in the background. The 233ms loop
                // never waits for the network, it just picks the lyrics up once they land.
                lyricState.Track(info.Identity);

                if (config.ShowLyrics && isNetEase && lyricState.TryClaim(info.Identity, now))
                {
                    _ = FetchLyrics(lyricState, info.Identity);
                }

                var lyrics     = config.ShowLyrics ? lyricState.Current : null;
                var lyricIndex = lyrics?.IndexOf(info.Schedule) ?? -1;

                if (info.Pause)
                {
                    // Pause means "show nothing": clear immediately (a state change
                    // flushes right away); repeated paused loops are no-ops.
                    if (sentIdentity is not null)
                    {
                        rpcClient.ClearPresence();

                        sentIdentity = null;
                        sentLyric    = -1;
                        lastSendTick = now;
                    }
                }
                else
                {
                    // Transmit immediately when the presence no longer matches reality;
                    // otherwise resync every few seconds as a safety net so a dropped
                    // update or accumulated drift heals itself.
                    var songChanged     = !string.Equals(sentIdentity, info.Identity, StringComparison.Ordinal);
                    var durationChanged = !songChanged && Math.Abs(info.Duration - sentDuration) > 1.0;

                    // Seek detection: playback advances linearly, so predict the position
                    // from the last send; drift beyond the threshold means the user sought,
                    // the same song restarted (single-track repeat), or a long rebuffer.
                    var predicted = sentSchedule + ((now - lastSendTick) / 1000.0);
                    var seeked    = !songChanged && Math.Abs(info.Schedule - predicted) > SeekThresholdSeconds;

                    var lyricChanged = !songChanged && lyricIndex != sentLyric;
                    var urgent       = songChanged || durationChanged || seeked;

                    if (!urgent && !lyricChanged && now - lastSendTick < ResyncIntervalMs)
                    {
                        continue;
                    }

                    // lyric-only advances are throttled; staying dirty makes the next
                    // loop send them as soon as the interval has elapsed.
                    if (!urgent && now - lastSendTick < MinSendIntervalMs)
                    {
                        continue;
                    }

                    // the "timg" badge asset only exists on the upstream applications; a
                    // custom app id would render a missing-asset placeholder instead.
                    var stockAssets = isNetEase
                        ? string.Equals(config.NetEaseAppId, DefaultNetEaseAppId, StringComparison.Ordinal)
                        : string.Equals(config.TencentAppId, DefaultTencentAppId, StringComparison.Ordinal);

                    rpcClient.Update(rpc => BuildPresence(rpc, info, config, lyrics, lyricIndex, stockAssets));

                    sentIdentity  = info.Identity;
                    sentSchedule  = info.Schedule;
                    sentDuration  = info.Duration;
                    sentLyric     = lyricIndex;
                    lastSendTick  = now;
                    lastRpcClient = rpcClient;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                // 用户就喜欢超低内存占用
                // 但是实际上来说并没有什么卵用
                GC.Collect();
                GC.WaitForFullGCComplete();

                await Task.Delay(TimeSpan.FromMilliseconds(233));
            }
        }
    }

    private static async Task FetchLyrics(LyricState state, string songId)
    {
        var lyrics = await NetEaseLyrics.FetchAsync(songId).ConfigureAwait(false);

        state.Complete(songId, lyrics);
    }

    private static string Pick(string? value, string fallback)
    {
        var trimmed = value?.Trim();

        // A wrong id makes Discord silently drop every activity: the process looks
        // healthy, the tray icon is there, and nothing ever shows up. Only accept
        // something shaped like a snowflake, anything else falls back to the built-in id.
        return trimmed is { Length: >= 17 and <= 20 } && trimmed.All(char.IsAsciiDigit)
            ? trimmed
            : fallback;
    }

    private static void BuildPresence(RichPresence rpc,
                                      PlayerInfo   info,
                                      Configurations config,
                                      SongLyrics?  lyrics,
                                      int          lyricIndex,
                                      bool         stockAssets)
    {
        var lyric = lyrics is not null && lyricIndex >= 0
            ? config.ShowTranslation && lyrics.TranslationAt(lyricIndex).Length > 0
                ? lyrics.TranslationAt(lyricIndex)
                : lyrics.TextAt(lyricIndex)
            : string.Empty;

        var lyricLine = lyric.Length > 0 && config.LyricsLayout != LyricsPlacement.ButtonLabel;

        rpc.Details = config.LyricsLayout == LyricsPlacement.ArtistInDetails && info.Artists.Length > 0
            ? $"🎵 {info.Title} · {info.Artists}".TruncateByUtf8Bytes(MaxRpcTextBytes)
            : $"🎵 {info.Title}".TruncateByUtf8Bytes(MaxRpcTextBytes);

        if (lyricLine)
        {
            rpc.State = $"🎶 {lyric}".TruncateByUtf8Bytes(MaxRpcTextBytes);
        }
        else if (config.LyricsLayout == LyricsPlacement.ArtistInTooltip)
        {
            // the artist already rides along on the album line here, so it must not get
            // a line of its own: no lyric means no second line at all.
            rpc.State = null;
        }
        else
        {
            rpc.State = $"🎤 {info.Artists}".TruncateByUtf8Bytes(MaxRpcTextBytes);
        }

        rpc.Type = ActivityType.Listening;

        // single UtcNow sample so end - start == Duration exactly
        var start = DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(info.Schedule));

        rpc.Timestamps = new Timestamps(start, start.Add(TimeSpan.FromSeconds(info.Duration)));

        var albumText = config.LyricsLayout == LyricsPlacement.ArtistInTooltip && info.Artists.Length > 0
            ? $"💿 {info.Album} · {info.Artists}"
            : $"💿 {info.Album}";

        rpc.Assets = new Assets
        {
            // fall back to Discord's default artwork when the cover url exceeds the limit
            LargeImageKey  = info.Cover.WithinLength(MaxRpcImageKeyBytes) ? info.Cover : string.Empty,
            LargeImageText = albumText.TruncateByUtf8Bytes(MaxRpcTextBytes),
            SmallImageKey  = stockAssets ? "timg" : null,
            SmallImageText = stockAssets ? "NetEase CloudMusic" : null,
        };

        var listen = new Button
        {
            Label = "🎧 Listen",
            Url   = info.Url,
        };

        // the only slot that renders *below* the progress bar is a button label
        if (config.LyricsLayout == LyricsPlacement.ButtonLabel && lyric.Length > 0)
        {
            rpc.Buttons =
            [
                listen,
                new Button
                {
                    Label = lyric.TruncateByUtf8Bytes(MaxRpcButtonBytes),
                    Url   = info.Url,
                },
            ];
        }
        else
        {
            rpc.Buttons = [listen];
        }
    }

    /// <summary>
    ///     Lyrics of the current song, written by a background task and read by the
    ///     polling loop. Results that arrive after the song changed are discarded.
    /// </summary>
    private sealed class LyricState
    {
        private readonly object _gate = new();

        private string?     _songId;
        private SongLyrics? _lyrics;
        private string?     _requestedId;
        private long        _retryAt;

        public SongLyrics? Current
        {
            get
            {
                lock (_gate)
                {
                    return _lyrics;
                }
            }
        }

        public void Track(string songId)
        {
            lock (_gate)
            {
                if (string.Equals(_songId, songId, StringComparison.Ordinal))
                {
                    return;
                }

                _songId      = songId;
                _lyrics      = null;
                _requestedId = null;
                _retryAt     = 0;
            }
        }

        public bool TryClaim(string songId, long now)
        {
            lock (_gate)
            {
                if (_lyrics is not null || _requestedId == songId || now < _retryAt)
                {
                    return false;
                }

                _requestedId = songId;

                return true;
            }
        }

        public void Complete(string songId, SongLyrics? lyrics)
        {
            lock (_gate)
            {
                if (!string.Equals(_songId, songId, StringComparison.Ordinal))
                {
                    return; // song changed while downloading: drop it
                }

                if (lyrics is null)
                {
                    _requestedId = null;
                    _retryAt     = Environment.TickCount64 + LyricRetryDelayMs;

                    return;
                }

                _lyrics = lyrics;
            }
        }
    }
}
