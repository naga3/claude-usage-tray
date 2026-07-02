#!/usr/bin/env bash
# WSL 側から実行するセットアップスクリプト
# 1. statusline.sh を ~/.claude/ に配置し settings.json に登録（フォールバック用）
# 2. ClaudeUsageTray.exe をビルドして Windows 側 (%LOCALAPPDATA%\ClaudeUsageTray) に配置
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

echo "==> ビルド"
"$REPO_DIR/build.sh"

echo "==> 既存インスタンスを停止"
powershell.exe -NoProfile -Command "
  Stop-Process -Name ClaudeUsageTray -Force -ErrorAction SilentlyContinue
  Get-CimInstance Win32_Process -Filter \"Name='powershell.exe'\" |
    Where-Object { \$_.ProcessId -ne \$PID -and \$_.CommandLine -like '*-File *ClaudeUsageTray.ps1*' } |
    ForEach-Object { Stop-Process -Id \$_.ProcessId -Force -ErrorAction SilentlyContinue }" | tr -d '\r'

echo "==> Windows 側へ配置"
LOCALAPPDATA_WIN=$(powershell.exe -NoProfile -Command 'Write-Host -NoNewline $env:LOCALAPPDATA' | tr -d '\r')
DEST=$(wslpath "$LOCALAPPDATA_WIN")/ClaudeUsageTray
mkdir -p "$DEST"
cp "$REPO_DIR/dist/ClaudeUsageTray.exe" "$DEST/"
# 旧 PowerShell 版の残骸を掃除
rm -f "$DEST/ClaudeUsageTray.ps1" "$DEST/ClaudeUsageTray.vbs"

HOME_WIN=$(echo "$HOME" | tr '/' '\\')
cat > "$DEST/ClaudeUsageTray.cfg" <<EOF
JsonPath=\\\\wsl.localhost\\${WSL_DISTRO_NAME}${HOME_WIN}\\.claude\\usage-monitor\\latest.json
CredPath=\\\\wsl.localhost\\${WSL_DISTRO_NAME}${HOME_WIN}\\.claude\\.credentials.json
# タスクバー上のテキスト帯を使う場合は 0 に（既定はトレイアイコンのみ）
NoWidget=1
EOF
DEST_WIN=$(wslpath -w "$DEST")
echo "    $DEST_WIN"

echo "==> スタートアップ登録"
powershell.exe -NoProfile -Command "
  \$startup = [Environment]::GetFolderPath('Startup')
  Remove-Item -Path (Join-Path \$startup 'ClaudeUsageTray.vbs') -ErrorAction SilentlyContinue
  \$ws = New-Object -ComObject WScript.Shell
  \$lnk = \$ws.CreateShortcut((Join-Path \$startup 'ClaudeUsageTray.lnk'))
  \$lnk.TargetPath = '${DEST_WIN}\\ClaudeUsageTray.exe'
  \$lnk.WorkingDirectory = '${DEST_WIN}'
  \$lnk.Save()
  Write-Host ('    ' + \$startup + '\ClaudeUsageTray.lnk')" | tr -d '\r'

echo "==> 起動"
(cd "$(wslpath "$LOCALAPPDATA_WIN")" && cmd.exe /c start '' "$DEST_WIN\\ClaudeUsageTray.exe")

echo "==> トレイアイコンを常時表示に昇格 (Win11)"
sleep 3
powershell.exe -NoProfile -Command "
  \$base = 'HKCU:\Control Panel\NotifyIconSettings'
  if (Test-Path \$base) {
    Get-ChildItem \$base | ForEach-Object {
      \$p = Get-ItemProperty \$_.PSPath
      if (\$p.ExecutablePath -like '*ClaudeUsageTray.exe') {
        Set-ItemProperty \$_.PSPath -Name IsPromoted -Value 1 -Type DWord
        Write-Host ('    promoted: ' + \$_.PSChildName)
      }
    }
  } else {
    Write-Host '    NotifyIconSettings なし (Win10?) — 手動でドラッグして常時表示にしてください'
  }" | tr -d '\r'

echo "完了。トレイ（時計の並び）にリングゲージアイコンが出ます。"
