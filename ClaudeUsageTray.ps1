# Claude Usage Tray
# Shows Claude Code 5-hour session usage (%) as a Windows tray icon.
# Reads the JSON dumped by statusline.sh (via \\wsl.localhost\...).
# NOTE: keep this file ASCII-only (PowerShell 5.1 misreads UTF-8 without BOM).

param(
    [string]$JsonPath = "\\wsl.localhost\Ubuntu-24.04\home\naga3\.claude\usage-monitor\latest.json",
    [int]$IntervalMs = 5000
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -Name Native -Namespace Win32 -MemberDefinition '[DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);'

# single instance
$script:mutex = New-Object System.Threading.Mutex($false, 'Global\ClaudeUsageTrayMutex')
if (-not $script:mutex.WaitOne(0, $false)) { exit }

function New-UsageIcon([string]$text, [System.Drawing.Color]$color) {
    $bmp = New-Object System.Drawing.Bitmap 32, 32
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $g.Clear([System.Drawing.Color]::Transparent)
        $fontSize = if ($text.Length -ge 3) { 15 } else { 20 }
        $font = New-Object System.Drawing.Font('Segoe UI', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $brush = New-Object System.Drawing.SolidBrush $color
        $sf = New-Object System.Drawing.StringFormat
        $sf.Alignment = [System.Drawing.StringAlignment]::Center
        $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
        $rect = New-Object System.Drawing.RectangleF 0, 0, 32, 32
        $g.DrawString($text, $font, $brush, $rect, $sf)
        $font.Dispose(); $brush.Dispose(); $sf.Dispose()
    } finally {
        $g.Dispose()
    }
    $hIcon = $bmp.GetHicon()
    $bmp.Dispose()
    return @{ Icon = [System.Drawing.Icon]::FromHandle($hIcon); Handle = $hIcon }
}

$script:prevHandle = [IntPtr]::Zero
function Set-TrayIcon([string]$text, [System.Drawing.Color]$color, [string]$tip) {
    $r = New-UsageIcon $text $color
    $script:notify.Icon = $r.Icon
    if ($tip.Length -gt 63) { $tip = $tip.Substring(0, 63) }  # NotifyIcon.Text hard limit
    $script:notify.Text = $tip
    if ($script:prevHandle -ne [IntPtr]::Zero) {
        [Win32.Native]::DestroyIcon($script:prevHandle) | Out-Null
    }
    $script:prevHandle = $r.Handle
}

function ConvertTo-LocalTime($resetsAt) {
    # resets_at: unix epoch seconds (number) or ISO string, depending on version
    try {
        if ($resetsAt -is [string] -and $resetsAt -notmatch '^\d+$') {
            return [DateTimeOffset]::Parse($resetsAt).LocalDateTime
        }
        return [DateTimeOffset]::FromUnixTimeSeconds([long]$resetsAt).LocalDateTime
    } catch { return $null }
}

function Update-Tray {
    $text = '-'; $color = [System.Drawing.Color]::Gray; $tip = 'Claude usage: no data'
    try {
        $raw = [System.IO.File]::ReadAllText($JsonPath, [System.Text.Encoding]::UTF8)
        $j = $raw | ConvertFrom-Json
        $fh = $j.rate_limits.five_hour
        if ($null -ne $fh -and $null -ne $fh.used_percentage) {
            $pct = [int][math]::Round([double]$fh.used_percentage)
            $reset = ConvertTo-LocalTime $fh.resets_at
            $wk = ''
            $sd = $j.rate_limits.seven_day
            if ($null -ne $sd -and $null -ne $sd.used_percentage) {
                $wk = ' | wk ' + [int][math]::Round([double]$sd.used_percentage) + '%'
            }
            $upd = [System.IO.File]::GetLastWriteTime($JsonPath).ToString('HH:mm')
            if ($null -ne $reset -and (Get-Date) -ge $reset) {
                # 5h window already reset; real value arrives on next Claude response
                $text = '0'; $color = [System.Drawing.Color]::Gray
                $tip = "5h: reset done (was $pct%)$wk | upd $upd"
            } else {
                $text = "$pct"
                $color = if ($pct -ge 90) { [System.Drawing.Color]::OrangeRed }
                         elseif ($pct -ge 70) { [System.Drawing.Color]::Orange }
                         else { [System.Drawing.Color]::White }
                $resetStr = if ($null -ne $reset) { ' (reset ' + $reset.ToString('HH:mm') + ')' } else { '' }
                $tip = "5h $pct%$resetStr$wk | upd $upd"
            }
        }
    } catch {
        # WSL not running / file missing / broken JSON -> keep gray '-'
    }
    Set-TrayIcon $text $color $tip
}

$script:notify = New-Object System.Windows.Forms.NotifyIcon
$script:notify.Visible = $true

$menu = New-Object System.Windows.Forms.ContextMenuStrip
$miOpen = $menu.Items.Add('Open latest JSON')
$miOpen.Add_Click({ try { Start-Process notepad.exe -ArgumentList $JsonPath } catch {} })
$miRefresh = $menu.Items.Add('Refresh now')
$miRefresh.Add_Click({ Update-Tray })
$menu.Items.Add('-') | Out-Null
$miExit = $menu.Items.Add('Exit')
$miExit.Add_Click({
    $script:timer.Stop()
    $script:notify.Visible = $false
    $script:notify.Dispose()
    [System.Windows.Forms.Application]::Exit()
})
$script:notify.ContextMenuStrip = $menu

$script:timer = New-Object System.Windows.Forms.Timer
$script:timer.Interval = $IntervalMs
$script:busy = $false
$script:timer.Add_Tick({
    if ($script:busy) { return }
    $script:busy = $true
    try { Update-Tray } finally { $script:busy = $false }
})

Update-Tray
$script:timer.Start()

$appContext = New-Object System.Windows.Forms.ApplicationContext
[System.Windows.Forms.Application]::Run($appContext)

if ($script:prevHandle -ne [IntPtr]::Zero) { [Win32.Native]::DestroyIcon($script:prevHandle) | Out-Null }
$script:mutex.ReleaseMutex()
