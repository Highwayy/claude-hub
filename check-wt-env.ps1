# check-wt-env.ps1
# 检查 Windows Terminal 相关的环境变量

$ErrorActionPreference = 'SilentlyContinue'

# 检查 WT 相关环境变量
$wtVars = Get-ChildItem Env: | Where-Object { $_.Name -like '*WT*' -or $_.Name -like '*TERM*' -or $_.Name -like '*CONSOLE*' }

Write-Host "WT/TERM/CONSOLE environment variables:"
foreach ($v in $wtVars) {
    Write-Host "  $($v.Name)=$($v.Value)"
}

# 检查 session ID
Write-Host "`nProcess info:"
Write-Host "  PID=$PID"
Write-Host "  PPID=$((Get-CimInstance Win32_Process -Filter "ProcessId=$PID").ParentProcessId)"

# 尝试获取 WT_SESSION 环境变量
$wtSession = $env:WT_SESSION
if ($wtSession) {
    Write-Host "  WT_SESSION=$wtSession"
} else {
    Write-Host "  WT_SESSION=not set"
}