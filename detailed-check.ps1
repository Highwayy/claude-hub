# detailed-check.ps1
$ErrorActionPreference = 'SilentlyContinue'

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class W {
    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumCallback cb, IntPtr p);
    [DllImport("user32.dll", CharSet=CharSet.Auto)]
    public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    public delegate bool EnumCallback(IntPtr h, IntPtr p);
}
"@

function Get-ProcessChain($pid) {
    $chain = @()
    $current = $pid
    for ($i = 0; $i -lt 20; $i++) {
        $proc = Get-Process -Id $current -ErrorAction SilentlyContinue
        if (-not $proc) { break }
        $chain += "pid=$current name=$($proc.ProcessName)"
        $parent = (Get-CimInstance Win32_Process -Filter "ProcessId=$current").ParentProcessId
        if (-not $parent -or $parent -eq $current) { break }
        $current = $parent
    }
    return $chain
}

Write-Host "=== Node process chains ===" -ForegroundColor Yellow
Get-Process -Name node -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "`nNode pid=$($_.Id):"
    Get-ProcessChain $_.Id | ForEach-Object { Write-Host "  $_" }
}

Write-Host "`n=== Terminal windows ===" -ForegroundColor Yellow
[W]::EnumWindows({
    param($h, $p)
    $sb = New-Object System.Text.StringBuilder 256
    [W]::GetClassName($h, $sb, 256) | Out-Null
    $cls = $sb.ToString()
    if ($cls -match "CASCADIA|ConsoleWindow|PseudoConsole") {
        $pid = 0
        [W]::GetWindowThreadProcessId($h, [ref]$pid) | Out-Null
        $proc = Get-Process -Id $pid -ErrorAction SilentlyContinue
        Write-Host "hwnd=$h class=$cls pid=$pid name=$($proc.ProcessName)"
    }
    return $true
}, [IntPtr]::Zero) | Out-Null