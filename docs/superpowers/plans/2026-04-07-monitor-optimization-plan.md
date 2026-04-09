# Claude Monitor 优化实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复窗口句柄关联 Bug，增强 Waiting 状态显示，简化右键菜单并添加删除功能

**Architecture:** 通过 PID 直接关联窗口句柄（不使用前台窗口回退）；SessionControl 边框闪烁 + 标题提示；简化右键菜单为单一删除功能

**Tech Stack:** Node.js, PowerShell, C# WinForms

---

## 文件结构

| 文件 | 修改类型 | 职责 |
|------|----------|------|
| `find-window-by-pid.ps1` | 新增 | 通过进程 PID 获取窗口句柄 |
| `hook.js` | 修改 | 只用 PID 关联，删除前台窗口逻辑 |
| `server.js` | 修改 | 新增 DELETE /session/:id 接口 |
| `Monitor.cs` | 修改 | Waiting 边框闪烁 + 标题提示 + 菜单简化 |

---

### Task 1: 创建 PowerShell 脚本获取窗口句柄

**Files:**
- Create: `E:\Code\claude-monitor\find-window-by-pid.ps1`

- [ ] **Step 1: 创建 find-window-by-pid.ps1 文件**

```powershell
# find-window-by-pid.ps1
# 通过进程 PID 获取主窗口句柄
param([int]$pid)

if ($pid -le 0) {
    exit 0
}

try {
    $proc = Get-Process -Id $pid -ErrorAction SilentlyContinue
    if ($proc) {
        $handle = $proc.MainWindowHandle
        if ($handle -and $handle -ne 0) {
            Write-Output $handle.ToInt64()
        }
    }
} catch {
    # 进程不存在或已退出
}
```

- [ ] **Step 2: 测试脚本可用性**

Run: `powershell -ExecutionPolicy Bypass -File "E:\Code\claude-monitor\find-window-by-pid.ps1" -pid <当前 Claude Code 进程 PID>`
Expected: 输出一个数字（窗口句柄）或无输出（进程无效）

- [ ] **Step 3: Commit**

```bash
git add find-window-by-pid.ps1
git commit -m "feat: add PowerShell script to find window by PID"
```

---

### Task 2: 修改 hook.js 只用 PID 关联窗口

**Files:**
- Modify: `E:\Code\claude-monitor\hook.js` (删除 `getForegroundWindowHandle`, `getAllClaudeWindowHandles` 函数，新增 `getWindowHandleByPid` 函数)

- [ ] **Step 1: 新增 getWindowHandleByPid 函数**

在 hook.js 第 17 行附近（`getAllClaudeWindowHandles` 函数定义之前）添加：

```javascript
// 通过 PID 获取窗口句柄
function getWindowHandleByPid(pid) {
    try {
        if (process.platform !== 'win32' || !pid) return null;
        const scriptPath = path.join(HOOK_DIR, 'find-window-by-pid.ps1');
        const result = execSync(
            'powershell -ExecutionPolicy Bypass -File "' + scriptPath + '" -pid ' + pid,
            { encoding: 'utf8', timeout: 3000 }
        );
        const handle = result.trim();
        if (handle && handle !== '0') {
            log('getWindowHandleByPid: pid=' + pid + ', handle=' + handle);
            return handle;
        }
        log('getWindowHandleByPid: no handle for pid=' + pid);
    } catch (e) {
        log('getWindowHandleByPid error: ' + e.message);
    }
    return null;
}
```

- [ ] **Step 2: 删除 getForegroundWindowHandle 函数**

删除 hook.js 第 46-64 行的 `getForegroundWindowHandle` 函数整体。

- [ ] **Step 3: 删除 getAllClaudeWindowHandles 函数**

删除 hook.js 第 19-43 行的 `getAllClaudeWindowHandles` 函数整体。

- [ ] **Step 4: 删除 enum-windows.ps1 引用**

删除 hook.js 第 26 行附近的 `enum-windows.ps1` 引用代码。

- [ ] **Step 5: 修改 SessionStart 事件处理逻辑**

找到 hook.js 中 `if (hookEvent === 'SessionStart')` 相关代码块（约第 688-889 行），替换窗口捕获逻辑：

找到这段代码块的开头：
```javascript
if (needCaptureWindow) {
    log('Capturing window handle...');
```

替换整个 `if (needCaptureWindow)` 块为：

```javascript
if (needCaptureWindow) {
    log('Capturing window handle by PID...');
    
    // 尝试获取 Claude Code 进程 PID
    // Claude Code 可能通过环境变量或进程树获取
    const claudePid = process.env.CLAUDE_PID || null;
    
    // 如果没有 CLAUDE_PID，尝试从 parent process 获取
    // 注意：hook.js 是被 Claude Code 调用的，所以 parent 可能是 Claude Code
    let targetPid = claudePid;
    if (!targetPid) {
        try {
            // 获取当前进程的父进程 PID
            const parentPidResult = execSync(
                'powershell -Command "(Get-Process -Id $pid).Parent.ProcessId"',
                { encoding: 'utf8', timeout: 2000 }
            );
            targetPid = parseInt(parentPidResult.trim());
            log('Parent PID: ' + targetPid);
        } catch (e) {
            log('Failed to get parent PID: ' + e.message);
        }
    }
    
    // 通过 PID 获取窗口句柄
    if (targetPid) {
        const windowHandle = getWindowHandleByPid(targetPid);
        if (windowHandle) {
            status.windowHandle = windowHandle;
            log('Captured window handle: ' + windowHandle + ' for session: ' + input.session_id);
        } else {
            status.windowHandle = null;
            log('No window handle captured (PID=' + targetPid + ') for session: ' + input.session_id);
        }
    } else {
        status.windowHandle = null;
        log('No PID available for window capture');
    }
} else if (hookEvent === 'SessionStart') {
    log('SessionStart with source=' + sessionSource + ', keeping existing window handle');
}
```

- [ ] **Step 6: 测试 hook.js 修改**

Run: `node E:\Code\claude-monitor\hook.js` (检查语法错误)
Expected: 无错误输出

- [ ] **Step 7: Commit**

```bash
git add hook.js
git commit -m "fix: use PID-based window handle association only"
```

---

### Task 3: 修改 server.js 添加删除会话接口

**Files:**
- Modify: `E:\Code\claude-monitor\server.js`

- [ ] **Step 1: 在 server.js 添加 DELETE /session/:id 接口**

在 `app.post('/session')` 后面（约第 238 行）添加：

```javascript
// Delete a single session
app.delete('/session/:id', (req, res) => {
    const sid = req.params.id;
    if (sessions[sid]) {
        delete sessions[sid];
        activeSessionIds.delete(sid);
        res.json({ success: true });
    } else {
        res.json({ success: false, error: 'Session not found' });
    }
});
```

- [ ] **Step 2: 测试服务器启动**

Run: `node E:\Code\claude-monitor\server.js`
Expected: 输出 "Monitor server: http://localhost:18989"

- [ ] **Step 3: 测试 DELETE 接口**

Run: `curl -X DELETE http://localhost:18989/session/test-id`
Expected: `{"success":false,"error":"Session not found"}`

- [ ] **Step 4: Commit**

```bash
git add server.js
git commit -m "feat: add DELETE /session/:id endpoint"
```

---

### Task 4: 修改 Monitor.cs - Waiting 状态边框闪烁

**Files:**
- Modify: `E:\Code\claude-monitor\Monitor.cs` (SessionControl 类)

- [ ] **Step 1: 在 SessionControl 类添加边框闪烁字段**

找到 SessionControl 类定义（约第 996 行），在 `_requiredHeight` 字段后添加：

```csharp
private bool _flashBorder = false;
private Color _borderColor = Color.FromArgb(243, 156, 18); // #f39c12
```

- [ ] **Step 2: 修改 SessionControl 的 UpdateData 方法**

找到 UpdateData 方法中 `waiting` 状态的处理（约第 1155 行）：

```csharp
if (data.state == "idle") { statusLbl.Text = "Idle"; }
else if (data.state == "waiting") { statusLbl.Text = "Waiting"; taskLbl.Text = "⏳ " + taskText; }
```

修改为：

```csharp
if (data.state == "idle") { 
    statusLbl.Text = "Idle"; 
    _flashBorder = false;
    statusLbl.ForeColor = Color.FromArgb(180, 180, 180);
}
else if (data.state == "waiting") { 
    statusLbl.Text = "⏳ Waiting";
    _flashBorder = true;
    statusLbl.ForeColor = Color.FromArgb(243, 156, 18); // 橙色
}
```

- [ ] **Step 3: 添加 OnPaint 方法绘制边框**

在 SessionControl 类的构造函数后（约第 1061 行后）添加：

```csharp
protected override void OnPaint(PaintEventArgs e)
{
    base.OnPaint(e);
    
    if (_flashBorder && _animTick % 2 == 0)
    {
        // 绘制橙色边框（2px）
        using (Pen pen = new Pen(_borderColor, 2))
        {
            e.Graphics.DrawRectangle(pen, 1, 1, this.Width - 2, this.Height - 2);
        }
    }
}
```

需要在 SessionControl 类开头添加 `_animTick` 字段（如果没有）：

```csharp
private int _animTick = 0;
```

- [ ] **Step 4: 在 Animate 方法中增加 _animTick**

修改 Animate 方法（约第 1078 行）：

```csharp
public void Animate()
{
    _animTick++;
    horse.Animate();
    if (_flashBorder) this.Invalidate(); // 触发重绘
}
```

- [ ] **Step 5: 编译测试**

Run: `csc /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll Monitor.cs`
Expected: 编译成功，生成 Monitor.exe

- [ ] **Step 6: Commit**

```bash
git add Monitor.cs
git commit -m "feat: add waiting state border flash effect"
```

---

### Task 5: 修改 Monitor.cs - 标题栏 Waiting 提示

**Files:**
- Modify: `E:\Code\claude-monitor\Monitor.cs` (ClaudeMonitor 类)

- [ ] **Step 1: 在 ClaudeMonitor 类添加 Waiting 提示字段**

找到 ClaudeMonitor 类定义（约第 12 行），在 `pinBtn` 字段后添加：

```csharp
private Label waitingLabel;
private bool hasWaitingSession = false;
```

- [ ] **Step 2: 在构造函数中添加 Waiting 提示标签**

找到构造函数中 `header.Controls.AddRange` 后面（约第 226 行），添加：

```csharp
// Waiting 提示标签（初始隐藏）
waitingLabel = new Label();
waitingLabel.Text = " ⏳ Waiting for Input";
waitingLabel.Width = 140;
waitingLabel.Height = 24;
waitingLabel.Dock = DockStyle.Left;
waitingLabel.BackColor = Color.FromArgb(243, 156, 18);
waitingLabel.ForeColor = Color.White;
waitingLabel.Font = new Font("Segoe UI", 9, FontStyle.Bold);
waitingLabel.Visible = false;
header.Controls.Add(waitingLabel);
```

- [ ] **Step 3: 在 UpdateSessionUI 方法中检测 Waiting 状态**

找到 UpdateSessionUI 方法（约第 557 行），在 `for` 循环结束后添加：

```csharp
// 检测是否有 waiting 状态的会话
hasWaitingSession = sessions.Any(s => s.state == "waiting");
waitingLabel.Visible = hasWaitingSession;
```

需要添加 `using System.Linq;` 到文件顶部。

- [ ] **Step 4: 编译测试**

Run: `csc /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll Monitor.cs`
Expected: 编译成功

- [ ] **Step 5: Commit**

```bash
git add Monitor.cs
git commit -m "feat: add waiting state title bar indicator"
```

---

### Task 6: 修改 Monitor.cs - 右键菜单简化

**Files:**
- Modify: `E:\Code\claude-monitor\Monitor.cs`

- [ ] **Step 1: 删除现有右键菜单项**

找到 ContextMenuStrip 定义（约第 233-243 行），删除整个 contextMenu 创建代码块：

```csharp
ContextMenuStrip contextMenu = new ContextMenuStrip();
ToolStripMenuItem resetItem = new ToolStripMenuItem("Reset Status");
resetItem.Click += (s, e) => ResetStatus();
ToolStripMenuItem copyItem = new ToolStripMenuItem("Copy Task");
copyItem.Click += (s, e) => CopyCurrentTask();
ToolStripMenuItem linkItem = new ToolStripMenuItem("Link to Active Window");
linkItem.Click += (s, e) => LinkActiveWindow();
ToolStripMenuItem flashItem = new ToolStripMenuItem("Flash Linked Window");
flashItem.Click += (s, e) => FlashLinkedWindow();
contextMenu.Items.AddRange(new ToolStripItem[] { resetItem, copyItem, linkItem, flashItem });
this.ContextMenuStrip = contextMenu;
```

替换为：

```csharp
// Session 右键菜单（在 SessionControl 上显示）
// 主窗口无右键菜单
this.ContextMenuStrip = null;
```

- [ ] **Step 2: 在 SessionControl 类添加右键菜单**

找到 SessionControl 构造函数（约第 1014-1060 行），在 `this.Controls.AddRange` 后添加：

```csharp
// 右键菜单 - 删除会话
ContextMenuStrip sessionMenu = new ContextMenuStrip();
ToolStripMenuItem deleteItem = new ToolStripMenuItem("删除会话");
deleteItem.Click += (s, e) => DeleteSession();
sessionMenu.Items.Add(deleteItem);
this.ContextMenuStrip = sessionMenu;
```

- [ ] **Step 3: 在 SessionControl 类添加 DeleteSession 方法**

在 SessionControl 类中添加：

```csharp
public void DeleteSession()
{
    if (!string.IsNullOrEmpty(_sessionId))
    {
        try
        {
            var request = (HttpWebRequest)WebRequest.Create("http://localhost:18989/session/" + _sessionId);
            request.Proxy = null;
            request.Timeout = 1000;
            request.Method = "DELETE";
            using (var response = request.GetResponse()) { }
        }
        catch { }
    }
}
```

- [ ] **Step 4: 删除不再需要的方法**

删除 ClaudeMonitor 类中的 `ResetStatus`, `CopyCurrentTask`, `LinkActiveWindow`, `FlashLinkedWindow` 方法（约第 400-704 行）。

- [ ] **Step 5: 编译测试**

Run: `csc /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll Monitor.cs`
Expected: 编译成功

- [ ] **Step 6: Commit**

```bash
git add Monitor.cs
git commit -m "feat: simplify context menu to single delete option"
```

---

### Task 7: 整体测试

- [ ] **Step 1: 启动服务器**

Run: `node E:\Code\claude-monitor\server.js`

- [ ] **Step 2: 启动监控窗口**

Run: `E:\Code\claude-monitor\Monitor.exe`

- [ ] **Step 3: 模拟 Waiting 状态**

Run: `curl -X POST http://localhost:18989/session -H "Content-Type: application/json" -d "{\"sessionId\":\"test\",\"state\":\"waiting\",\"task\":\"Waiting for input\"}"`

Expected: 监控窗口显示边框闪烁 + 标题栏橙色提示

- [ ] **Step 4: 测试删除功能**

在监控窗口右键点击会话，选择 "删除会话"
Expected: 会话从监控窗口消失

- [ ] **Step 5: 验证 API**

Run: `curl http://localhost:18989/status`
Expected: 已删除的会话不在 sessions 列表中

---

### Task 8: 最终 Commit

- [ ] **Step 1: 提交所有修改**

```bash
git add -A
git commit -m "feat: claude monitor optimization complete

- fix: window handle association using PID only
- feat: waiting state border flash + title indicator
- feat: simplify context menu with delete option"
```