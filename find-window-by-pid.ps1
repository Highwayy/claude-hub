# find-terminal-window.ps1
# Find the CASCADIA_HOSTING_WINDOW_CLASS window for a specific Claude session
# Walks process tree to find the OpenConsole/conhost for this tab, then maps to CASCADIA window

param([int]$processId)

$ErrorActionPreference = 'SilentlyContinue'

# Find terminal window handles by walking process tree UPWARD from the given PID
# Stop at the FIRST process that has a MainWindowHandle or is conhost/OpenConsole
function Get-TerminalPid($pid) {
    $current = $pid
    $visited = @{}
    for ($i = 0; $i -lt 30; $i++) {
        if ($visited.ContainsKey($current)) { break }
        $visited[$current] = $true

        $proc = Get-Process -Id $current -ErrorAction SilentlyContinue
        if ($proc) {
            # Check if this is a console host process
            if ($proc.ProcessName -eq 'OpenConsole' -or $proc.ProcessName -eq 'conhost') {
                # This is the conhost for this specific tab
                # Walk its children to find one with a visible window
                $children = Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $current }
                foreach ($child in $children) {
                    $childProc = Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue
                    if ($childProc -and $childProc.MainWindowHandle -ne 0) {
                        Write-Output $childProc.MainWindowHandle.ToInt64()
                        return
                    }
                }
                # If no child has a window, try the conhost itself
                if ($proc.MainWindowHandle -ne 0) {
                    Write-Output $proc.MainWindowHandle.ToInt64()
                    return
                }
            }
            # Also check the process itself
            if ($proc.MainWindowHandle -ne 0) {
                Write-Output $proc.MainWindowHandle.ToInt64()
                return
            }
        }

        $parent = (Get-CimInstance Win32_Process -Filter "ProcessId=$current" -ErrorAction SilentlyContinue).ParentProcessId
        if (-not $parent -or $parent -eq $current -or $parent -eq 0) { break }
        $current = $parent
    }
    return $null
}

$h = Get-TerminalPid $processId
if ($h) { Write-Output $h }
