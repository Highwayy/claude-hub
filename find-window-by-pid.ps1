# find-window-by-pid.ps1
# Get main window handle by process PID
param([int]$processId)

if ($processId -le 0) {
    exit 0
}

try {
    $proc = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($proc) {
        $handle = $proc.MainWindowHandle
        if ($handle -and $handle -ne 0) {
            Write-Output $handle.ToInt64()
        }
    }
} catch {
    # Process not found or exited
}