#!/usr/bin/env bash
# ClaudeUsageTray.exe を Windows 標準の csc.exe (.NET Framework 4.8) でビルドする
# 追加インストール不要。csc は UNC (\\wsl.localhost) 上で動かないため Windows の TEMP でビルドする。
set -euo pipefail

REPO_DIR="$(cd "$(dirname "$0")" && pwd)"
CSC='/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe'
[ -x "$CSC" ] || { echo "csc.exe が見つかりません"; exit 1; }

WINTMP=$(powershell.exe -NoProfile -Command 'Write-Host -NoNewline $env:TEMP' | tr -d '\r')
BUILD="$(wslpath "$WINTMP")/claude-usage-tray-build"
mkdir -p "$BUILD" "$REPO_DIR/dist"
cp "$REPO_DIR/ClaudeUsageTray.cs" "$BUILD/"

# アプリアイコン生成（32x32 に "5h" を描画）
cat > "$BUILD/gen-icon.ps1" <<'EOF'
Add-Type -AssemblyName System.Drawing
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$bmp = New-Object System.Drawing.Bitmap 32, 32
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.Clear([System.Drawing.Color]::FromArgb(30, 30, 30))
$font = New-Object System.Drawing.Font('Segoe UI', 15, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$sf = New-Object System.Drawing.StringFormat
$sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
$g.DrawString('5h', $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 0, 32, 32), $sf)
$g.Dispose()
$ico = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
$fs = [System.IO.File]::Create((Join-Path $dir 'app.ico'))
$ico.Save($fs)
$fs.Close()
EOF
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w "$BUILD/gen-icon.ps1")"

(cd "$BUILD" && "$CSC" /nologo /target:winexe /optimize+ \
  /win32icon:app.ico \
  /out:ClaudeUsageTray.exe \
  /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll \
  ClaudeUsageTray.cs)

cp "$BUILD/ClaudeUsageTray.exe" "$REPO_DIR/dist/"
echo "OK: dist/ClaudeUsageTray.exe ($(stat -c%s "$REPO_DIR/dist/ClaudeUsageTray.exe") bytes)"
