# Claude Usage Tray
# Shows Claude Code 5-hour session usage (%) as a Windows tray icon,
# plus an always-on-top text strip overlaid on the taskbar (like ElevenClock).
# Reads the JSON dumped by statusline.sh (via \\wsl.localhost\...).
# NOTE: keep this file ASCII-only (PowerShell 5.1 misreads UTF-8 without BOM).

param(
    [string]$JsonPath = "\\wsl.localhost\Ubuntu-24.04\home\naga3\.claude\usage-monitor\latest.json",
    [string]$CredPath = "\\wsl.localhost\Ubuntu-24.04\home\naga3\.claude\.credentials.json",
    [int]$IntervalMs = 5000,
    [int]$ApiIntervalSec = 60,
    [switch]$NoWidget
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -Name Native -Namespace Win32 -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
[DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
[DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
[DllImport("user32.dll")] public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
[DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string lpszWindow);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
'@

# single instance
$script:mutex = New-Object System.Threading.Mutex($false, 'Global\ClaudeUsageTrayMutex')
if (-not $script:mutex.WaitOne(0, $false)) { exit }

$script:cfgDir = Join-Path $env:LOCALAPPDATA 'ClaudeUsageTray'
$script:posFile = Join-Path $script:cfgDir 'widget-pos.txt'

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

# primary source: undocumented OAuth usage endpoint (works even when Claude Code
# is idle or used via the VSCode extension, which never fires the statusline hook)
$script:apiUsage = $null
$script:apiFetchedAt = [DateTime]::MinValue
function Get-ApiUsage {
    if (((Get-Date) - $script:apiFetchedAt).TotalSeconds -lt $ApiIntervalSec) { return }
    $script:apiFetchedAt = Get-Date
    try {
        $cred = [System.IO.File]::ReadAllText($CredPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        $tok = $cred.claudeAiOauth.accessToken
        if (-not $tok) { return }
        $r = Invoke-RestMethod -Uri 'https://api.anthropic.com/api/oauth/usage' -TimeoutSec 5 -Headers @{
            Authorization = "Bearer $tok"; 'anthropic-beta' = 'oauth-2025-04-20'
        }
        if ($null -ne $r.five_hour -and $null -ne $r.five_hour.utilization) {
            $script:apiUsage = @{
                pct    = [double]$r.five_hour.utilization
                reset  = $r.five_hour.resets_at
                wpct   = $r.seven_day.utilization
                at     = Get-Date
            }
        }
    } catch {
        # WSL down / token expired / endpoint gone -> fall back to statusline file
    }
}

function Update-Tray {
    $text = '-'; $color = [System.Drawing.Color]::Gray; $tip = 'Claude usage: no data'
    $wtext = '5h -'
    Get-ApiUsage
    $pct = $null; $reset = $null; $wk = ''; $upd = ''; $src = ''
    if ($null -ne $script:apiUsage -and ((Get-Date) - $script:apiUsage.at).TotalSeconds -lt 300) {
        $pct = [int][math]::Round([double]$script:apiUsage.pct)
        $reset = ConvertTo-LocalTime $script:apiUsage.reset
        if ($null -ne $script:apiUsage.wpct) {
            $wk = ' | wk ' + [int][math]::Round([double]$script:apiUsage.wpct) + '%'
        }
        $upd = $script:apiUsage.at.ToString('HH:mm'); $src = 'api'
    } else {
        try {
            $raw = [System.IO.File]::ReadAllText($JsonPath, [System.Text.Encoding]::UTF8)
            $j = $raw | ConvertFrom-Json
            $fh = $j.rate_limits.five_hour
            if ($null -ne $fh -and $null -ne $fh.used_percentage) {
                $pct = [int][math]::Round([double]$fh.used_percentage)
                $reset = ConvertTo-LocalTime $fh.resets_at
                $sd = $j.rate_limits.seven_day
                if ($null -ne $sd -and $null -ne $sd.used_percentage) {
                    $wk = ' | wk ' + [int][math]::Round([double]$sd.used_percentage) + '%'
                }
                $upd = [System.IO.File]::GetLastWriteTime($JsonPath).ToString('HH:mm'); $src = 'file'
            }
        } catch {
            # WSL not running / file missing / broken JSON -> keep gray '-'
        }
    }
    if ($null -ne $pct) {
        if ($src -eq 'file' -and $null -ne $reset -and (Get-Date) -ge $reset) {
            # 5h window already reset; real value arrives on next statusline write
            $text = '0'; $color = [System.Drawing.Color]::Gray
            $tip = "5h: reset done (was $pct%)$wk | $src $upd"
            $wtext = "5h 0%$wk"
        } else {
            $text = "$pct"
            $color = if ($pct -ge 90) { [System.Drawing.Color]::OrangeRed }
                     elseif ($pct -ge 70) { [System.Drawing.Color]::Orange }
                     else { [System.Drawing.Color]::White }
            $resetStr = if ($null -ne $reset) { ' (reset ' + $reset.ToString('HH:mm') + ')' } else { '' }
            $tip = "5h $pct%$resetStr$wk | $src $upd"
            $wtext = "5h $pct%$wk"
        }
    }
    Set-TrayIcon $text $color $tip
    if ($null -ne $script:wlabel) {
        $script:wlabel.Text = $wtext
        $script:wlabel.ForeColor = $color
        $script:widget.TopMost = $true  # re-assert above the taskbar
    }
}

$script:notify = New-Object System.Windows.Forms.NotifyIcon
$script:notify.Visible = $true

# --- taskbar text widget (always-on-top strip overlaid on the taskbar) ---
$script:widget = $null
$script:wlabel = $null
if (-not $NoWidget) {
    $script:widget = New-Object System.Windows.Forms.Form
    $script:widget.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
    $script:widget.ShowInTaskbar = $false
    $script:widget.TopMost = $true
    $script:widget.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
    $script:widget.BackColor = [System.Drawing.Color]::FromArgb(16, 16, 16)
    $script:widget.AutoSize = $true
    $script:widget.AutoSizeMode = [System.Windows.Forms.AutoSizeMode]::GrowAndShrink

    $script:wlabel = New-Object System.Windows.Forms.Label
    $script:wlabel.AutoSize = $true
    $script:wlabel.Font = New-Object System.Drawing.Font('Segoe UI', 9, [System.Drawing.FontStyle]::Bold)
    $script:wlabel.ForeColor = [System.Drawing.Color]::Gray
    $script:wlabel.BackColor = $script:widget.BackColor
    $script:wlabel.Text = '5h -'
    $script:wlabel.Padding = New-Object System.Windows.Forms.Padding 6, 3, 6, 3
    $script:widget.Controls.Add($script:wlabel)

    # do not steal focus (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW)
    $script:widget.Add_HandleCreated({
        $ex = [Win32.Native]::GetWindowLong($script:widget.Handle, -20)
        [Win32.Native]::SetWindowLong($script:widget.Handle, -20, $ex -bor 0x08000000 -bor 0x80) | Out-Null
    })

    # position: saved -> restore, else snapped next to the tray area after first show
    $script:hasSavedPos = $false
    try {
        if (Test-Path $script:posFile) {
            $xy = ([System.IO.File]::ReadAllText($script:posFile)).Split(',')
            $script:widget.Location = New-Object System.Drawing.Point ([int]$xy[0]), ([int]$xy[1])
            $script:hasSavedPos = $true
        }
    } catch {}

    # drag to move, save position on release
    $script:dragOff = $null
    $script:wlabel.Add_MouseDown({
        param($s, $e)
        if ($e.Button -eq [System.Windows.Forms.MouseButtons]::Left) { $script:dragOff = $e.Location }
    })
    $script:wlabel.Add_MouseMove({
        param($s, $e)
        if ($null -ne $script:dragOff) {
            $p = [System.Windows.Forms.Cursor]::Position
            $script:widget.Location = New-Object System.Drawing.Point ($p.X - $script:dragOff.X), ($p.Y - $script:dragOff.Y)
        }
    })
    $script:wlabel.Add_MouseUp({
        param($s, $e)
        if ($null -ne $script:dragOff) {
            $script:dragOff = $null
            try {
                if (-not (Test-Path $script:cfgDir)) { New-Item -ItemType Directory -Path $script:cfgDir | Out-Null }
                [System.IO.File]::WriteAllText($script:posFile, "$($script:widget.Location.X),$($script:widget.Location.Y)")
            } catch {}
        }
    })
}

$menu = New-Object System.Windows.Forms.ContextMenuStrip
$miOpen = $menu.Items.Add('Open latest JSON')
$miOpen.Add_Click({ try { Start-Process notepad.exe -ArgumentList $JsonPath } catch {} })
$miRefresh = $menu.Items.Add('Refresh now')
$miRefresh.Add_Click({ $script:apiFetchedAt = [DateTime]::MinValue; Update-Tray })
if ($null -ne $script:widget) {
    $miWidget = $menu.Items.Add('Show/Hide taskbar text')
    $miWidget.Add_Click({ $script:widget.Visible = -not $script:widget.Visible })
}
$menu.Items.Add('-') | Out-Null
$miExit = $menu.Items.Add('Exit')
$miExit.Add_Click({
    $script:timer.Stop()
    $script:notify.Visible = $false
    $script:notify.Dispose()
    if ($null -ne $script:widget) { $script:widget.Close() }
    [System.Windows.Forms.Application]::Exit()
})
$script:notify.ContextMenuStrip = $menu
if ($null -ne $script:wlabel) { $script:wlabel.ContextMenuStrip = $menu }

$script:timer = New-Object System.Windows.Forms.Timer
$script:timer.Interval = $IntervalMs
$script:busy = $false
$script:timer.Add_Tick({
    if ($script:busy) { return }
    $script:busy = $true
    try { Update-Tray } finally { $script:busy = $false }
})

function Snap-WidgetToTray {
    # place the widget just left of the notification area (^ / IME / battery block)
    try {
        $sb = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
        $x = $wa.Right - 430  # fallback
        $tray = [Win32.Native]::FindWindow('Shell_TrayWnd', $null)
        if ($tray -ne [IntPtr]::Zero) {
            $na = [Win32.Native]::FindWindowEx($tray, [IntPtr]::Zero, 'TrayNotifyWnd', $null)
            if ($na -ne [IntPtr]::Zero) {
                $rect = New-Object -TypeName 'Win32.Native+RECT'
                if ([Win32.Native]::GetWindowRect($na, [ref]$rect) -and $rect.Left -gt 0) {
                    $x = $rect.Left - $script:widget.Width - 8
                }
            }
        }
        if ($wa.Bottom -lt $sb.Bottom) {
            $y = $wa.Bottom + [int](($sb.Bottom - $wa.Bottom - $script:widget.Height) / 2)
        } else {
            $y = $sb.Bottom - 60  # auto-hide taskbar etc: float above bottom edge
        }
        $script:widget.Location = New-Object System.Drawing.Point $x, $y
    } catch {}
}

if ($null -ne $script:widget) { $script:widget.Show() }
Update-Tray
if ($null -ne $script:widget -and -not $script:hasSavedPos) { Snap-WidgetToTray }
$script:timer.Start()

$appContext = New-Object System.Windows.Forms.ApplicationContext
[System.Windows.Forms.Application]::Run($appContext)

if ($script:prevHandle -ne [IntPtr]::Zero) { [Win32.Native]::DestroyIcon($script:prevHandle) | Out-Null }
$script:mutex.ReleaseMutex()
