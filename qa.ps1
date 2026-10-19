$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class T {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    public static string D(IntPtr h) { RECT r; GetWindowRect(h, out r); return (r.R - r.L) + "x" + (r.B - r.T) + "@" + r.L; }
    public static bool Vis(IntPtr h) { return IsWindowVisible(h); }
    public static IntPtr FindPid(int pid, int minW) {
        IntPtr f = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if ((int)p != pid) return true;
            RECT r; GetWindowRect(h, out r);
            if ((r.R - r.L) >= minW) { f = h; return false; }
            return true;
        }, IntPtr.Zero);
        return f;
    }
    public static System.Collections.Generic.List<IntPtr> AllWins(int pid) {
        var l2 = new System.Collections.Generic.List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr l) { uint p; GetWindowThreadProcessId(h, out p); if ((int)p != pid) return true; RECT r; GetWindowRect(h, out r); if (r.R - r.L <= 1) return true; l2.Add(h); return true; }, IntPtr.Zero);
        return l2;
    }
    public static IntPtr FindTitle(string part) {
        IntPtr f = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            if (!IsWindowVisible(h)) return true;
            System.Text.StringBuilder sb = new System.Text.StringBuilder(512);
            GetWindowTextW(h, sb, 512);
            string t = sb.ToString();
            if (t.IndexOf(part) >= 0 && t.IndexOf("AGENT HUB") < 0 && t.Length > 0) { f = h; return false; }
            return true;
        }, IntPtr.Zero);
        return f;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
    public static void LClick(IntPtr h, int x, int y) {
        IntPtr lp = (IntPtr)((y << 16) | (x & 0xFFFF));
        SendMessage(h, 0x0201, (IntPtr)1, lp);
        SendMessage(h, 0x0202, IntPtr.Zero, lp);
    }
    public static void RClick(IntPtr h, int x, int y) {
        IntPtr lp = (IntPtr)((y << 16) | (x & 0xFFFF));
        SendMessage(h, 0x0204, (IntPtr)2, lp);
        SendMessage(h, 0x0205, IntPtr.Zero, lp);
    }
    public static void MouseTo(IntPtr h, int x, int y) {
        SendMessage(h, 0x0200, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xFFFF)));
    }
}
'@
[void][T]::SetProcessDPIAware()
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# 期望尺寸都是按 96 DPI 写的（侧栏 92px、滑出后 x=-89），缩放一改这些数就全错
$g = [System.Drawing.Graphics]::FromHwnd([IntPtr]::Zero)
$dpi = $g.DpiX
$g.Dispose()
if ($dpi -ne 96) { "需要 100% 缩放再跑（当前 DPI=$dpi）"; exit 1 }

# 分辨率不写死，从当前环境算
$scr = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$full    = "$($scr.Width)x$($scr.Height)@0"          # 空状态铺满工作区
$side    = "92x$($scr.Height)@0"                     # 有应用时只占侧栏
$content = "$($scr.Width - 92)x$($scr.Height)@92"    # 应用窗口铺在内容区
$parked  = "92x$($scr.Height)@-89"                   # 滑出屏幕左缘

# 仓库根目录 = 脚本所在目录；测试实例丢到临时目录，不碰仓库里的 bin\
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$mark = 'agenthub-qa'
$dir  = Join-Path $env:TEMP $mark
Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $dir -Force | Out-Null
Copy-Item (Join-Path $repo 'bin\AgentHub.exe') (Join-Path $dir 'AgentHub.exe')
@'
[
 {"id":"np1","name":"记事本","short":"N1","kind":"exe","exe":"C:\\Windows\\System32\\notepad.exe","proc":"notepad","title":"记事本"},
 {"id":"np2","name":"字符映射表","short":"N2","kind":"exe","exe":"C:\\Windows\\System32\\charmap.exe","proc":"charmap","title":"字符映射表"}
]
'@ | Set-Content (Join-Path $dir 'agents.json') -Encoding UTF8
# 实例带 --config 启动，运行期数据在 data-<配置名>\（见 Program.SetDataDir）
New-Item -ItemType Directory -Path (Join-Path $dir 'data-agents') -Force | Out-Null
'{"Chosen":[],"Shown":null,"Order":["np1","np2"]}' | Set-Content (Join-Path $dir 'data-agents\session.json') -Encoding UTF8

$results = New-Object System.Collections.ArrayList
function Check($name, $cond, $detail) {
    [void]$results.Add([pscustomobject]@{ Test = $name; Pass = [bool]$cond; Detail = $detail })
}

$exe = Join-Path $dir 'AgentHub.exe'
Start-Process -FilePath $exe -ArgumentList ('--config "' + (Join-Path $dir 'agents.json') + '"')
Start-Sleep -Seconds 4

$proc = Get-Process -Name AgentHub -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*$mark*" } | Select-Object -First 1
if (-not $proc) { 'FATAL: 测试实例没起来'; exit 1 }
$dock = $proc.MainWindowHandle

# 1 空状态铺满
Check '空状态铺满工作区' ([T]::D($dock) -eq $full) ([T]::D($dock))

# 2 点第一个应用：起进程并被接管
[T]::LClick($dock, 46, 56)
Start-Sleep -Seconds 4
$n1 = Get-Process -Name notepad -ErrorAction SilentlyContinue | Select-Object -First 1
$w1 = if ($n1) { [T]::FindPid($n1.Id, 500) } else { [IntPtr]::Zero }
Check '点图标能启动并接管' ($n1 -ne $null -and $w1 -ne [IntPtr]::Zero -and [T]::D($w1) -eq $content) ("dock=" + [T]::D($dock) + " app=" + $(if ($w1 -ne [IntPtr]::Zero) { [T]::D($w1) } else { '无' }))
Check '有应用时侧栏只占 92px' ([T]::D($dock) -eq $side) ([T]::D($dock))
Check '侧栏常驻（不自动滑出）' ([T]::D($dock) -like '*@0') ([T]::D($dock))

# 3 点第二个应用：切换，第一个被隐藏
[T]::LClick($dock, 46, 120)
Start-Sleep -Seconds 4
$n2 = Get-Process -Name charmap -ErrorAction SilentlyContinue | Select-Object -First 1
$w2 = if ($n2) { [T]::FindPid($n2.Id, 500) } else { [IntPtr]::Zero }
Check '第二个应用能接管' ($n2 -ne $null) ("charmap 进程=" + $(if ($n2) { '有' } else { '无' }))
Check '切走的应用被隐藏' ($n1 -and -not [T]::Vis($w1)) ("notepad vis=" + [T]::Vis($w1))

# 4 点回第一个（注意：MRU 会把它挪到第一位，第二项的坐标才是它）
[T]::LClick($dock, 46, 120)
Start-Sleep -Seconds 2
Check '切回已选应用直接显示' ([T]::Vis($w1) -and [T]::D($w1) -eq $content) ([T]::D($w1) + " vis=" + [T]::Vis($w1))

# 5 电源键：滑出 + 应用隐藏 + 进程存活
[T]::LClick($dock, 46, 1430)
Start-Sleep -Seconds 2
Check '电源键滑出侧栏' ([T]::D($dock) -eq $parked) ([T]::D($dock))
Check '后台时应用被隐藏' (-not [T]::Vis($w1)) ("vis=" + [T]::Vis($w1))
Check '后台时进程存活' ((Get-Process -Name AgentHub -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*$mark*" } | Measure-Object).Count -eq 1) ''

# 6 热区唤回
$dock = (Get-Process -Name AgentHub | Where-Object { $_.Path -like "*$mark*" } | Select-Object -First 1).MainWindowHandle
[T]::MouseTo($dock, 90, 400)
Start-Sleep -Seconds 2
Check '热区能唤回' ([T]::D($dock) -like '*@0') ([T]::D($dock))
Check '唤回后应用恢复显示' ([T]::Vis($w1)) ("vis=" + [T]::Vis($w1))

# 7 任务栏开关
[T]::SendMessage($dock, 0x0112, [IntPtr]0xF020, [IntPtr]::Zero)
Start-Sleep -Seconds 2
Check '任务栏点击滑出' ([T]::D($dock) -like '*@-89') ([T]::D($dock))
[T]::SendMessage($dock, 0x0112, [IntPtr]0xF020, [IntPtr]::Zero)
Start-Sleep -Seconds 2
Check '任务栏再点滑入' ([T]::D($dock) -like '*@0') ([T]::D($dock))

# 8 右键取消已选应用
[T]::RClick($dock, 46, 120)
Start-Sleep -Milliseconds 800
$p2 = Get-Process -Name AgentHub | Where-Object { $_.Path -like "*$mark*" } | Select-Object -First 1
# 菜单是同一进程的另一个顶层窗口，枚举找不是主窗口的那个
$menu = [IntPtr]::Zero
foreach ($h in [T]::AllWins($p2.Id)) { if ($h -ne $dock) { $menu = $h; break } }
Check '右键菜单弹出了' ($menu -ne [IntPtr]::Zero) ("hwnd=" + $menu)

if ($menu -ne [IntPtr]::Zero) {
    [T]::LClick($menu, 20, 18)
    Start-Sleep -Seconds 2
    $alive = Get-Process -Name charmap -ErrorAction SilentlyContinue
    Check '取消会结束应用进程' ($null -eq $alive) ''
}

# 9 取消最后一个应用：回到空状态，不退出
$proc2 = Get-Process -Name AgentHub -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*$mark*" } | Select-Object -First 1
if ($proc2) {
    $dock = $proc2.MainWindowHandle
    [T]::RClick($dock, 46, 56)
    Start-Sleep -Milliseconds 800
    $m2 = [T]::FindTitle('取消')
    if ($m2 -ne [IntPtr]::Zero) {
        [T]::LClick($m2, 20, 18)
        Start-Sleep -Seconds 3
    }
    $alive2 = Get-Process -Name AgentHub -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*$mark*" }
    Check '取消最后一个应用后进程存活' ($null -ne $alive2) ''
    if ($alive2) {
        $dock = $alive2.MainWindowHandle
        Check '取消最后一个应用后回到空状态' ([T]::D($dock) -eq $full) ([T]::D($dock))
    }
}

$results | Format-Table -AutoSize
'--- 失败项 ---'
$bad = $results | Where-Object { -not $_.Pass }
if ($bad) { $bad | Format-Table -AutoSize } else { '（无）' }
'--- 清理 ---'
Get-Process -Name AgentHub -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*$mark*" } | Stop-Process -Force
Get-Process -Name notepad, charmap -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
'完成'
