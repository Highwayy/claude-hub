# get-console-window.ps1
# 使用 GetConsoleWindow 获取当前进程的控制台窗口句柄

$ErrorActionPreference = 'SilentlyContinue'

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class ConsoleWin {
    [DllImport("kernel32.dll")]
    public static extern IntPtr GetConsoleWindow();
}
"@

$handle = [ConsoleWin]::GetConsoleWindow()
Write-Output $handle.ToInt64()