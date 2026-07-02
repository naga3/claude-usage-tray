#!/usr/bin/env bash
# アンインストール（WSL 側から実行）
set -euo pipefail

echo "==> トレイアプリを終了"
powershell.exe -NoProfile -Command "
  Stop-Process -Name ClaudeUsageTray -Force -ErrorAction SilentlyContinue
  Get-CimInstance Win32_Process -Filter \"Name='powershell.exe'\" |
    Where-Object { \$_.ProcessId -ne \$PID -and \$_.CommandLine -like '*-File *ClaudeUsageTray.ps1*' } |
    ForEach-Object { Stop-Process -Id \$_.ProcessId -Force -ErrorAction SilentlyContinue }" | tr -d '\r' || true

echo "==> スタートアップ登録を削除"
STARTUP_WIN=$(powershell.exe -NoProfile -Command 'Write-Host -NoNewline ([Environment]::GetFolderPath("Startup"))' | tr -d '\r')
rm -f "$(wslpath "$STARTUP_WIN")/ClaudeUsageTray.lnk" "$(wslpath "$STARTUP_WIN")/ClaudeUsageTray.vbs"

echo "==> Windows 側のファイルを削除"
LOCALAPPDATA_WIN=$(powershell.exe -NoProfile -Command 'Write-Host -NoNewline $env:LOCALAPPDATA' | tr -d '\r')
rm -rf "$(wslpath "$LOCALAPPDATA_WIN")/ClaudeUsageTray"

echo "==> settings.json から statusLine を削除"
SETTINGS="$HOME/.claude/settings.json"
if jq -e '.statusLine.command == "bash ~/.claude/statusline.sh"' "$SETTINGS" >/dev/null 2>&1; then
  tmp=$(mktemp)
  jq 'del(.statusLine)' "$SETTINGS" > "$tmp" && mv "$tmp" "$SETTINGS"
  rm -f "$HOME/.claude/statusline.sh"
else
  echo "    statusLine が別物になっているためスキップ"
fi
rm -rf "$HOME/.claude/usage-monitor"

echo "完了。"
