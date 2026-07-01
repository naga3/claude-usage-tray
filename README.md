# claude-usage-tray

Claude Code（Pro/Max サブスク）の **5時間セッション使用率** を Windows のタスクトレイに常時表示するツール。WSL2 + Windows 環境用。

- トレイに使用率の数字を描画したアイコンが常駐（<70% 白 / 70%〜 オレンジ / 90%〜 赤）
- **タスクバー上に `5h 42% | wk 61%` のテキスト帯を常時表示**（ElevenClock 方式の最前面オーバーレイ。ドラッグで移動でき位置は記憶、フォーカスは奪わない、右クリックメニューで表示切替、`-NoWidget` で無効化）
- ホバーで詳細（5h 使用率・リセット時刻・週次使用率・最終更新）
- 右クリックで JSON 確認 / 手動更新 / 終了
- おまけで Claude Code の statusline にも `5h 42% | wk 61% | ctx 8%` を表示

## 仕組み

サブスクの残量を取れる公開 API は存在しないため、Claude Code 公式の **statusline 機構**をデータ源にする。

```
[Claude Code (WSL)]
  └─ 応答のたびに statusline.sh へ JSON を stdin で渡す
       ├─ ~/.claude/usage-monitor/latest.json へアトミック保存
       └─ statusline 表示文字列を stdout
[ClaudeUsageTray.ps1 (Windows / PowerShell 5.1 + WinForms)]
  └─ 5秒ごとに \\wsl.localhost\<distro>\...\latest.json を読んでトレイアイコンを更新
```

statusline の JSON には `rate_limits.five_hour.used_percentage` / `resets_at`（5時間ウィンドウ）、`rate_limits.seven_day.*`（週次）、`context_window` などが含まれる。

## インストール

WSL 側で:

```bash
./install.sh
```

やること:

1. `statusline.sh` を `~/.claude/` に配置し、`~/.claude/settings.json` に `statusLine` を登録（設定済みならスキップ）
2. `ClaudeUsageTray.ps1` + VBS ランチャーを `%LOCALAPPDATA%\ClaudeUsageTray\` にコピー
3. スタートアップフォルダに VBS を登録（ログオン時に自動起動、コンソール窓なし）
4. トレイアプリを即時起動

依存: WSL 側に `jq`。Windows 側は標準の PowerShell 5.1 のみ（ビルド不要）。

Win11 では新規トレイアイコンは隠しトレイ（`^`）に入るため、install.sh がレジストリ
`HKCU\Control Panel\NotifyIconSettings` の `IsPromoted=1` で常時表示領域（時計の隣）に昇格させる。
出ない場合は `^` から手動でドラッグするか、設定 → 個人用設定 → タスクバー → その他のシステムトレイアイコン で ON にする。

## アンインストール

```bash
./uninstall.sh
```

## 表示の意味

| 表示 | 意味 |
|---|---|
| 白/オレンジ/赤の数字 | 5時間ウィンドウの使用率% |
| グレーの `0` | リセット時刻を過ぎた（次の応答で実値に更新） |
| グレーの `-` | データなし（WSL 未起動 / Claude Code 未応答） |

## 制約

- statusline は **Claude Code が応答した時だけ**更新される。アイドル中は値が止まるが、使用率はアイドル中に増えないので実害はない（リセット越えはグレー `0` でカバー）
- `rate_limits` はサブスク認証時のみ・セッション初回応答後に出現
- 複数セッション並行時は last-write-wins（値はアカウント共通なので問題なし）
- `ClaudeUsageTray.ps1` は PowerShell 5.1 の文字コード事情（BOM なし UTF-8 を誤読）のため ASCII のみで記述している
- タスクバー本体への埋め込み API（DeskBand）は Win11 で廃止のため、テキスト帯は「タスクバーに重ねた最前面ウィンドウ」で実現している。全画面アプリの上にも出るので、動画視聴時などは右クリック → Show/Hide taskbar text で消せる
