# =====================================================================
# AGENT HUB 桌面快捷方式安装
#   1) 清除 exe 上可能存在的「以管理员身份运行」兼容标记（UAC 弹窗根源）
#   2) 创建普通桌面图标：双击直接启动，不弹提权框
#   3) 可选：再加一个「管理员」图标（计划任务方式，现在授权一次，
#      以后用它启动同样不弹提权框；AI 软件是管理员运行时才需要它）
# =====================================================================
param([switch]$SkipAdminOffer)
$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $PSScriptRoot
$Exe  = Join-Path $Root 'bin\AgentHub.exe'
$Bin  = Split-Path -Parent $Exe
$Icon = Join-Path $Root 'icon.ico'
$Vbs  = Join-Path $PSScriptRoot 'Launch-AgentHub.vbs'
$TaskName = 'AgentHub'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function New-Shortcut($Path, $Target, $Arguments, $WorkDir, $IconPath, $Desc) {
    $ws = New-Object -ComObject WScript.Shell
    $sc = $ws.CreateShortcut($Path)
    $sc.TargetPath = $Target
    if ($Arguments) { $sc.Arguments = $Arguments }
    if ($WorkDir)   { $sc.WorkingDirectory = $WorkDir }
    if ($IconPath -and (Test-Path $IconPath)) { $sc.IconLocation = $IconPath }
    $sc.Description = $Desc
    $sc.Save()
}

if (-not (Test-Path $Exe)) {
    Write-Host "[x] 找不到 $Exe" -ForegroundColor Red
    Write-Host "    请先运行项目根目录的 build.bat 编译。"
    exit 1
}

# ---- 1. 清除「以管理员身份运行」兼容标记 ----
foreach ($hive in @('HKCU:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers',
                    'HKLM:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers')) {
    try {
        $v = Get-ItemPropertyValue -Path $hive -Name $Exe -ErrorAction Stop
        if ($v -match 'RUNASADMIN') {
            Remove-ItemProperty -Path $hive -Name $Exe -Force
            Write-Host "[ok] 已清除管理员兼容标记: $hive" -ForegroundColor Green
        }
    } catch { }   # 没这个键或没权限都不影响
}

# ---- 2. 普通桌面图标（无需管理员，双击直接启动） ----
$desktop = [Environment]::GetFolderPath('Desktop')
$plain = Join-Path $desktop 'AgentHub.lnk'
New-Shortcut $plain $Exe '' $Bin $Icon 'AGENT HUB'
Write-Host "[ok] 桌面图标已创建: AgentHub（双击直接启动，不弹提权框）" -ForegroundColor Green

# ---- 3. 可选：管理员图标（计划任务方式） ----
if (Test-Admin) {
    try {
        $action = New-ScheduledTaskAction -Execute $Exe -WorkingDirectory $Bin
        $me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        $principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
        Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
        $adminLnk = Join-Path $desktop 'AgentHub (管理员).lnk'
        New-Shortcut $adminLnk "$env:SystemRoot\System32\wscript.exe" "`"$Vbs`"" $Bin $Icon 'AGENT HUB（管理员，免提权）'
        Write-Host "[ok] 桌面图标已创建: AgentHub (管理员)（同样不弹提权框）" -ForegroundColor Green
        Write-Host ""
        Write-Host "提示：如果你的 AI 软件是以管理员身份运行的，请用「AgentHub (管理员)」图标；"
        Write-Host "      否则用普通「AgentHub」图标即可。两个图标以后都不会再弹提权框。"
    } catch {
        Write-Host "[!] 管理员图标创建失败（不影响普通图标）: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}
elseif (-not $SkipAdminOffer) {
    Add-Type -AssemblyName System.Windows.Forms
    $r = [System.Windows.Forms.MessageBox]::Show(
        "普通桌面图标已经建好，双击即可直接启动、不弹提权框。`n`n如果你的 AI 软件是以「管理员身份」运行的，AGENT HUB 需要同级权限才能接管它们的窗口，这种情况建议再加一个「管理员」图标（通过系统计划任务实现：现在授权一次，以后用它启动也不弹提权框）。`n`n是否现在创建管理员图标？（选「是」会弹一次系统的用户账户控制提示）",
        'AGENT HUB 快捷方式', 'YesNo', 'Question')
    if ($r -eq 'Yes') {
        Start-Process powershell.exe -Verb RunAs -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"",'-SkipAdminOffer'
    } else {
        Write-Host "已跳过管理员图标。以后想补建，再运行一次本脚本即可。"
    }
}

Write-Host ""
Write-Host "完成。" -ForegroundColor Green
if ($SkipAdminOffer) { Write-Host ""; Read-Host "按回车键关闭此窗口" }
