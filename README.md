# claude-usage-tray

Claude Code（Pro/Max サブスク）の **5時間セッション使用率** を Windows のタスクバーに常時表示するツール。WSL2 + Windows 環境用。

本体は C# (WinForms) 製の単一 exe（約20KB）。**Windows 標準搭載の csc.exe (.NET Framework 4.8) でビルドするため、追加インストールは一切不要**（.NET SDK も Visual Studio も要らない）。初代の PowerShell 版は git 履歴（`ae16a33` 以前）にある。

- **タスクバー上に `5h 42% (2h14m)` のテキスト帯を常時表示**（使用率とリセットまでの残り時間。ElevenClock 方式の最前面オーバーレイ。ドラッグで移動でき位置は記憶、フォーカスは奪わない、右クリックメニューで表示切替、`-NoWidget` で無効化）
- **帯またはトレイアイコンを左クリックでフル詳細ポップアップ**（5h・週次それぞれの使用率／リセット時刻／残り時間、データソース、更新時刻。もう一度クリックで閉じる）
- トレイ（隠しトレイ）に使用率の数字を描画したアイコンが常駐、ホバーで概要ツールチップ
- 色分け: <70% 白 / 70%〜 オレンジ / 90%〜 赤
- 右クリックで JSON 確認 / 手動更新 / 帯の表示切替 / 終了
- おまけで Claude Code の statusline にも `5h 42% | wk 61% | ctx 8%` を表示

## 仕組み

データ源は2段構え。

**メイン: OAuth usage エンドポイント（非公式）** — `ClaudeUsageTray.ps1` が60秒ごとに
`~/.claude/.credentials.json` の OAuth トークン（`\\wsl.localhost` 経由で読む）で
`GET https://api.anthropic.com/api/oauth/usage`（ヘッダ `anthropic-beta: oauth-2025-04-20`）を叩く。
`five_hour.utilization` / `resets_at`、`seven_day.utilization` が返る。
アイドル中でも、VSCode 拡張しか使っていなくても常に最新値が取れる。
非公式なのでいつ消えても文句は言えない。

**フォールバック: statusline 機構（公式）** — API が使えないとき（トークン失効・エンドポイント廃止等）は
statusline が書き出したファイルに切り替える。ツールチップ末尾の `api` / `file` でどちらか分かる。

```
[Claude Code (ターミナルTUI)]
  └─ 応答のたびに statusline.sh へ JSON を stdin で渡す
       ├─ ~/.claude/usage-monitor/latest.json へアトミック保存
       └─ statusline 表示文字列を stdout（TUI下部に 5h 42% | wk 61% | ctx 8%）
[ClaudeUsageTray.exe (Windows / C# WinForms, .NET Framework 4.8)]
  ├─ 60秒ごと: OAuth usage API（バックグラウンドスレッド）→ ダメなら latest.json
  └─ 5秒ごと: 表示更新
```

パス等の設定は exe と同じディレクトリの `ClaudeUsageTray.cfg`（`キー=値` 形式、install.sh が生成）:

```
JsonPath=\\wsl.localhost\<distro>\home\<user>\.claude\usage-monitor\latest.json
CredPath=\\wsl.localhost\<distro>\home\<user>\.claude\.credentials.json
# IntervalMs=5000 / ApiIntervalSec=60 / NoWidget=1 も指定可
```

注意: **statusline はターミナル TUI 専用**で、VSCode 拡張のセッションでは呼ばれない（検証済み）。
statusline 側の JSON には `rate_limits.five_hour.used_percentage` / `resets_at`、`rate_limits.seven_day.*`、`context_window` などが含まれる。

## インストール

WSL 側で:

```bash
./install.sh
```

やること:

1. `statusline.sh` を `~/.claude/` に配置し、`~/.claude/settings.json` に `statusLine` を登録（設定済みならスキップ）
2. `build.sh` で `ClaudeUsageTray.cs` を csc.exe でコンパイル（Windows の TEMP でビルドして `dist/` に出力）
3. 旧インスタンスを停止し、exe + cfg を `%LOCALAPPDATA%\ClaudeUsageTray\` に配置
4. スタートアップフォルダにショートカットを登録（ログオン時に自動起動）
5. トレイアプリを即時起動

依存: WSL 側に `jq`。Windows 側は OS 標準搭載のもののみ。ビルドだけやり直す場合は `./build.sh`。

数字アイコンは Win11 の仕様で隠しトレイ（`^`）に入る。常時見えるのはテキスト帯の方なのでそのままでよいが、
アイコンも常時表示したい場合は `^` からドラッグするか、レジストリ `HKCU\Control Panel\NotifyIconSettings` の
該当エントリで `IsPromoted=1` にする。

## アンインストール

```bash
./uninstall.sh
```

## 表示の意味

| 表示 | 意味 |
|---|---|
| 白/オレンジ/赤の数字 | 5時間ウィンドウの使用率% |
| グレーの `0` | リセット時刻を過ぎた（fileソース時のみ。次の statusline 書き込みで実値に更新） |
| グレーの `-` | データなし（WSL 未起動 / API 不達かつ statusline 未書き込み） |

## 制約

- statusline は **Claude Code が応答した時だけ**更新される。アイドル中は値が止まるが、使用率はアイドル中に増えないので実害はない（リセット越えはグレー `0` でカバー）
- `rate_limits` はサブスク認証時のみ・セッション初回応答後に出現
- 複数セッション並行時は last-write-wins（値はアカウント共通なので問題なし）
- `ClaudeUsageTray.cs` は標準搭載 csc.exe の制約で **C# 5 構文のみ**（文字列補間 `$""` や `?.` は使えない）
- タスクバー本体への埋め込み API（DeskBand）は Win11 で廃止のため、テキスト帯は「タスクバーに重ねた最前面ウィンドウ」で実現している。全画面アプリの上にも出るので、動画視聴時などは右クリック → Show/Hide taskbar text で消せる
- exe は無署名なので、他マシンに配る場合は SmartScreen 警告が出る（自分でビルドすれば問題ない）
