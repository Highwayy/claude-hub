# Claude Monitor 优化设计

## 背景

Claude Monitor 是 Claude Code 的状态监控窗口，当前存在以下问题：
1. 窗口句柄关联不准确（两个会话的窗口句柄可能反了）
2. waiting 状态不够醒目，用户容易错过需要输入的时机
3. 右键菜单功能冗余，需要简化并增加删除功能

## 设计方案

### 1. 窗口句柄关联 Bug 修复

**问题**：两个会话启动时窗口句柄可能被错误关联

**方案**：只使用 PID 关联，获取不到准确句柄则返回空（不使用前台窗口回退）

**修改文件**：
- `hook.js`
- 新增 `find-window-by-pid.ps1`

**实现细节**：

1. hook.js 在 SessionStart 事件中获取 Claude Code 进程 PID
2. 调用新 PowerShell 脚本通过 PID 获取窗口句柄：
   ```powershell
   # find-window-by-pid.ps1
   param([int]$pid)
   $proc = Get-Process -Id $pid -ErrorAction SilentlyContinue
   if ($proc) {
       $handle = $proc.MainWindowHandle
       if ($handle -and $handle -ne 0) {
           Write-Output $handle.ToInt64()
       }
   }
   ```
3. 如果返回空或 0，session.windowHandle = null
4. 删除原有的前台窗口捕获逻辑和 `enum-windows.ps1` 的复杂匹配

### 2. Waiting 状态着重显示

**目标**：waiting 状态时边框闪烁 + 标题提示，提醒用户需要输入

**修改文件**：`Monitor.cs`

**实现细节**：

1. SessionControl 边框闪烁：
   - waiting 状态时绘制橙色边框（`#f39c12`）
   - 在 animationTimer 中交替显示/隐藏（250ms 周期）
   - 使用 Paint 事件绘制 2px 边框

2. 标题栏提示：
   - 检测是否有 waiting 状态的会话
   - 标题栏动态显示 "Waiting for Input" 文字（橙色背景）

3. 状态文字醒目：
   - waiting 状态文字改为橙色 `#f39c12`
   - 保持 "⏳" 前缀

### 3. 右键菜单简化 + 删除功能

**目标**：清除现有菜单项，只保留删除功能

**修改文件**：
- `Monitor.cs`
- `server.js`

**当前菜单项（删除）**：
- Reset Status
- Copy Task
- Link to Active Window
- Flash Linked Window

**新菜单项**：
- 删除会话：直接从服务器删除该会话记录

**实现细节**：

1. Monitor.cs：
   - SessionControl 右键菜单改为单一项 "删除会话"
   - 点击后调用 `DELETE /session/:id`

2. server.js：
   - 新增 `DELETE /session/:id` 接口
   - 直接从 sessions 对象中删除该条目
   - 同时从 activeSessionIds 中移除

## 实现顺序

1. 窗口句柄 Bug 修复（最紧急）
2. Waiting 状态显示
3. 右键菜单简化

## 文件修改清单

| 文件 | 修改类型 | 说明 |
|------|----------|------|
| hook.js | 修改 | 只用 PID 关联窗口 |
| find-window-by-pid.ps1 | 新增 | 通过 PID 获取窗口句柄 |
| Monitor.cs | 修改 | 边框闪烁 + 菜单简化 |
| server.js | 修改 | 新增 DELETE session 接口 |