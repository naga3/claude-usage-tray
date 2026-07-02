# claude-usage-tray

Claude Code（Pro/Max サブスク）の **5時間セッション使用率** を Windows のタスクバーに常時表示するツール。WSL2 + Windows 環境用。

本体は C# (WinForms) 製の単一 exe（約20KB）。**Windows 標準搭載の csc.exe (.NET Framework 4.8) でビルドするため、追加インストールは一切不要**（.NET SDK も Visual Studio も要らない）。初代の PowerShell 版は git 履歴（`ae16a33` 以前）にある。

- **リングゲージのトレイアイコンが常時表示**（時計の並びに昇格）。円弧の伸びが使用率、色が危険度（<70% 青 / 70%〜 黄 / 90%〜 赤）、中央に%数字。5秒ごとに再描画・データは60秒ごとに取得
- **左クリックでフル詳細ポップアップ**（5h・週次のメーターバー／リセット時刻／残り時間、データソース、更新時刻。もう一度クリックで閉じる）
- ホバーで概要ツールチップ
- 右クリックで JSON 確認 / 手動更新 / 終了
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
# IntervalMs=5000 / ApiIntervalSec=60 も指定可
```

注意: **statusline はターミナル TUI 専用**で、VSCode 拡張のセッションでは呼ばれない（検証済み）。
statusline 側の JSON には `rate_limits.five_hour.used_percentage` / `resets_at`、`rate_limits.seven_day.*`、`context_window` などが含まれる。

## インストール

### 手っ取り早く（zip 版）

[Releases](https://github.com/naga3/claude-usage-tray/releases) の zip を解凍して `ClaudeUsageTray.exe` を実行するだけ。
Claude Code のログイン情報（`%USERPROFILE%\.claude` → 各 WSL ディストロ）を自動検出する。
無署名のため SmartScreen 警告あり（詳細情報 → 実行）。自動起動は `shell:startup` にショートカットを置く。

### フルセットアップ（WSL 環境、statusline フォールバック込み）

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

トレイアイコンは Win11 の仕様では隠しトレイ（`^`）に入るが、install.sh がレジストリ
`HKCU\Control Panel\NotifyIconSettings` の `IsPromoted=1` で常時表示（時計の並び）に昇格させる。
出ない場合は `^` からドラッグするか、設定 → 個人用設定 → タスクバー → その他のシステムトレイアイコン で ON。

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
- exe は無署名なので、他マシンに配る場合は SmartScreen 警告が出る（自分でビルドすれば問題ない）
