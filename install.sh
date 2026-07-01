#!/usr/bin/env bash
# WSL 側から実行するセットアップスクリプト
# 1. statusline.sh を ~/.claude/ に配置し settings.json に登録
# 2. トレイアプリを Windows 側 (%LOCALAPPDATA%\ClaudeUsageTray) にコピー
# 3. スタートアップ登録 + 即時起動
set -euo pipefail

REPO_DIR="$(cd "$(dirname "$0")" && pwd)"
SETTINGS="$HOME/.claude/settings.json"

command -v jq >/dev/null || { echo "jq が必要です (sudo apt install jq)"; exit 1; }
[ -n "${WSL_DISTRO_NAME:-}" ] || { echo "WSL 上で実行してください"; exit 1; }

echo "==> statusline を配置"
mkdir -p "$HOME/.claude/usage-monitor"
cp "$REPO_DIR/statusline.sh" "$HOME/.claude/statusline.sh"
chmod +x "$HOME/.claude/statusline.sh"

if [ -f "$SETTINGS" ] && jq -e '.statusLine' "$SETTINGS" >/dev/null 2>&1; then
  echo "    settings.json: statusLine は設定済みのためスキップ（手動で確認してください）"
else
  tmp=$(mktemp)
  jq '.statusLine = {"type":"command","command":"bash ~/.claude/statusline.sh"}' "$SETTINGS" > "$tmp" && mv "$tmp" "$SETTINGS"
  echo "    settings.json に statusLine を登録"
fi

echo "==> Windows 側へトレイアプリを配置"
LOCALAPPDATA_WIN=$(powershell.exe -NoProfile -Command 'Write-Host -NoNewline $env:LOCALAPPDATA' | tr -d '\r')
DEST=$(wslpath "$LOCALAPPDATA_WIN")/ClaudeUsageTray
mkdir -p "$DEST"
cp "$REPO_DIR/ClaudeUsageTray.ps1" "$DEST/"

DEST_WIN=$(wslpath -w "$DEST")
JSON_WIN_PATH="\\\\wsl.localhost\\${WSL_DISTRO_NAME}$(echo "$HOME" | tr '/' '\\')\\.claude\\usage-monitor\\latest.json"

VBS="$DEST/ClaudeUsageTray.vbs"
cat > "$VBS" <<EOF
CreateObject("WScript.Shell").Run "powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File ""${DEST_WIN}\\ClaudeUsageTray.ps1"" -JsonPath ""${JSON_WIN_PATH}""", 0, False
EOF
echo "    $DEST_WIN"

echo "==> スタートアップ登録"
STARTUP_WIN=$(powershell.exe -NoProfile -Command 'Write-Host -NoNewline ([Environment]::GetFolderPath("Startup"))' | tr -d '\r')
cp "$VBS" "$(wslpath "$STARTUP_WIN")/"
echo "    $STARTUP_WIN"

echo "==> 起動"
(cd "$(wslpath "$LOCALAPPDATA_WIN")" && wscript.exe "$(wslpath -w "$VBS")")

echo "完了。タスクバー上にテキスト帯、隠しトレイに数字アイコンが出ます（データが来るまでは '-' 表示）。"
echo "Claude Code で1回応答すると実際の使用率に変わります。"
