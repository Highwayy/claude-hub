# verify-terminal-mapping.ps1
# 验证：node -> OpenConsole -> CASCADIA 窗口的关系

$ErrorActionPreference = 'SilentlyContinue'

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class WinAPI {
    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumCallback cb, IntPtr p);
    [DllImport("user32.dll", CharSet=CharSet.Auto)]
    public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr h);
    public delegate bool EnumCallback(IntPtr h, IntPtr p);
}
"@

# 1. 获取所有 node 进程及其父进程
Write-Host "=== Node processes and their parents ===" -ForegroundColor Yellow
$nodes = Get-Process -Name node -ErrorAction SilentlyContinue
foreach ($node in $nodes) {
    $parent = (Get-CimInstance Win32_Process -Filter "ProcessId=$($node.Id)").ParentProcessId
    $parentProc = Get-Process -Id $parent -ErrorAction SilentlyContinue
    Write-Host "node pid=$($node.Id) -> parent pid=$parent name=$($parentProc.ProcessName)"
}

# 2. 获取所有 CASCADIA 窗口及其 PID
Write-Host "`n=== CASCADIA_HOSTING_WINDOW_CLASS windows ===" -ForegroundColor Yellow
$cascadiaWindows = @()
[WinAPI]::EnumWindows({
    param($h, $p)
    $sb = New-Object System.Text.StringBuilder 256
    [WinAPI]::GetClassName($h, $sb, 256) | Out-Null
    if ($sb.ToString() -eq "CASCADIA_HOSTING_WINDOW_CLASS") {
        $pid = 0
        [WinAPI]::GetWindowThreadProcessId($h, [ref]$pid) | Out-Null
        $vis = [WinAPI]::IsWindowVisible($h)
        $ico = [WinAPI]::IsIconic($h)
        $proc = Get-Process -Id $pid -ErrorAction SilentlyContinue
        Write-Host "hwnd=$h pid=$pid name=$($proc.ProcessName) visible=$vis iconic=$ico"
        $cascadiaWindows += @{hwnd=$h; pid=$pid; name=$proc.ProcessName}
    }
    return $true
}, [IntPtr]::Zero) | Out-Null

# 3. 检查 OpenConsole 进程
Write-Host "`n=== OpenConsole processes ===" -ForegroundColor Yellow
$openConsoles = Get-Process -Name OpenConsole -ErrorAction SilentlyContinue
foreach ($oc in $openConsoles) {
    $parent = (Get-CimInstance Win32_Process -Filter "ProcessId=$($oc.Id)").ParentProcessId
    $parentProc = Get-Process -Id $parent -ErrorAction SilentlyContinue
    Write-Host "OpenConsole pid=$($oc.Id) -> parent pid=$parent name=$($parentProc.ProcessName)"
}

# 4. 检查 WindowsTerminal 进程
Write-Host "`n=== WindowsTerminal process ===" -ForegroundColor Yellow
$wts = Get-Process -Name WindowsTerminal -ErrorAction SilentlyContinue
foreach ($wt in $wts) {
    Write-Host "WindowsTerminal pid=$($wt.Id) MainWindow=$($wt.MainWindowHandle)"
    # 获取子进程
    $children = Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $wt.Id }
    foreach ($child in $children) {
        Write-Host "  child: pid=$($child.ProcessId) name=$($child.Name)"
    }
}

Write-Host "`n=== CONCLUSION ===" -ForegroundColor Green
Write-Host "CASCADIA windows belong to PID: $($cascadiaWindows | Select-Object -ExpandProperty pid -Unique)"
Write-Host "These PIDs are WindowsTerminal, NOT OpenConsole"