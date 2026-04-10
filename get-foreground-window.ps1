# Get foreground window handle and class name
param()

$ErrorActionPreference = 'Continue'

$dllPath = Join-Path $PSScriptRoot 'GetForeground.dll'

try {
    # Load the DLL
    Add-Type -Path $dllPath -ErrorAction Stop
    $result = [GetForeground.Window]::GetInfo()
    Write-Output $result
} catch {
    Write-Output "Error: $($_.Exception.Message)"
}