# MusicRpc

把网易云音乐 / QQ 音乐的播放状态同步到 Discord。

这是 [Kxnrl/NetEase-Cloud-Music-DiscordRPC](https://github.com/Kxnrl/NetEase-Cloud-Music-DiscordRPC) 的魔改版，在原版基础上主要是加了歌词显示。

## 和原版差在哪

- **Discord 卡片里能看到歌词了**。歌词从网易云官方接口拉，跟着播放进度走，不用装插件。
- **去掉了卡片上的 GitHub 推广按钮**，只留「Listen」跳转歌曲页。
- 顺手把配置保存修了。原版那个 `Configurations` 每次启动都会把配置文件覆写成 `{}`，等于从来没存住过，现在能正常读写。

## 歌词放哪

先说个前提：Discord 的活动卡片只有两行文字，进度条固定压在这两行下面，第三行放不了字。所以歌词只能占其中一行。

托盘图标右键 → `Lyrics Layout`，三个排法：

| 选项 | 效果 |
| --- | --- |
| `2nd line (artist in 1st line)` | 默认。第一行「歌名 · 歌手」，第二行滚歌词 |
| `2nd line (artist in tooltip)` | 第一行只留歌名，歌手挪到封面悬停提示 |
| `in button label` | 歌名歌手都不动，歌词做成第二个按钮 —— 唯一能显示在进度条下方的位置 |

`Show Translation` 打开后优先显示译文（网易云有译文才显示），默认关着。

## 用起来

需要 [.NET 10 桌面运行时 (x64)](https://dotnet.microsoft.com/download/dotnet/10.0)。

双击 `MusicRpc.exe` 就行，没有主窗口，托盘里多一个图标。第一次运行会把自己写进开机启动（`HKCU\...\Run`），不想要就在托盘右键 `AutoStart` 取消掉。

自己编：

```bash
git clone --recursive https://github.com/bedkillerspacex-boop/MusicRpc.git
cd MusicRpc
dotnet build Vanessa/Vanessa.csproj -c Release
```

要单文件 exe：

```bash
dotnet publish Vanessa/Vanessa.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

## 配置文件

在 `%LocalAppData%\Vanessa\config.json`。托盘菜单里改是立刻生效的，手改文件要重启：

```json
{
  "showLyrics": true,
  "showTranslation": false,
  "lyricsLayout": "artistInDetails"
}
```

## 几个已知问题

- 歌词只有网易云能取。QQ 音乐那边还是只显示歌手，它没有能对得上的接口。
- 歌词是整行跳的，不是逐字。Discord 卡片本来就要两秒才刷新一次，逐字没意义。
- 纯音乐、没歌词的歌会退回显示歌手，不会挂着「暂无歌词」。
- 连续切歌的时候状态更新会被压到 4 秒一次，这是 Discord 自己的频率限制，不是卡了。
- 歌词拉取失败（断网、接口抽风）不弹窗，就是安静地显示歌手，不影响正常同步。

## 版权

原项目是 [Kxnrl](https://github.com/Kxnrl) 写的，MIT 协议。这个 fork 只是加了自己想用的东西，`LICENSE` 没动，版权还是原来的。
