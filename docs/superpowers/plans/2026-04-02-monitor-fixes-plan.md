# Claude Monitor 修复与增强实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复多会话窗口自动关闭问题、点击激活窗口失效问题，并增加 model/context/branch 信息显示。

**Architecture:** 服务端协调 + 启动锁 + 进程 PID 检测解决竞争条件；Alt 键技巧解决窗口激活；扩展现有数据结构增加信息显示。

**Tech Stack:** Node.js (Express)、C# (WinForms)、Windows API (user32.dll)

---

## 文件结构

| 文件 | 职责 |
|------|------|
| `server.js` | 监控服务器，新增启动协调端点和状态管理 |
| `hook.js` | Claude Code hook，新增启动协调逻辑和信息提取 |
| `Monitor.cs` | 监控窗口，新增 PID 报告、Alt 键技巧、信息显示 UI |

---

## Task 1: server.js - 添加 Monitor 启动协调端点

**Files:**
- Modify: `E:\Code\claude-monitor\server.js`

- [ ] **Step 1: 添加状态变量**

在 `showWindowFlag` 变量后添加：

```javascript
// Monitor 进程协调
let monitorPid = null;
let monitorStarting = false;
let monitorStartTimeout = null;
```

- [ ] **Step 2: 添加 GET /monitor-status 端点**

在 `app.get('/monitor-status', ...)` 之前添加：

```javascript
// 获取 Monitor 进程状态
app.get('/monitor-status', (req, res) => {
    const MONITOR_TIMEOUT = 10000;
    const running = monitorPid && (Date.now() - monitorLastAlive < MONITOR_TIMEOUT);
    res.json({
        running: running,
        pid: monitorPid,
        starting: monitorStarting,
        lastAlive: monitorLastAlive
    });
});
```

- [ ] **Step 3: 添加 POST /monitor-starting 端点**

```javascript
// 设置 Monitor 启动锁
app.post('/monitor-starting', (req, res) => {
    monitorStarting = true;
    if (monitorStartTimeout) clearTimeout(monitorStartTimeout);
    monitorStartTimeout = setTimeout(() => {
        monitorStarting = false;
    }, 5000);
    res.json({ success: true });
});
```

- [ ] **Step 4: 添加 POST /monitor-started 端点**

```javascript
// Monitor 启动完成，报告 PID
app.post('/monitor-started', (req, res) => {
    const { pid } = req.body;
    if (pid) monitorPid = pid;
    monitorStarting = false;
    if (monitorStartTimeout) {
        clearTimeout(monitorStartTimeout);
        monitorStartTimeout = null;
    }
    res.json({ success: true });
});
```

- [ ] **Step 5: 修改 POST /monitor-heartbeat 端点接收 PID**

将现有的 `app.post('/monitor-heartbeat', ...)` 修改为：

```javascript
app.post('/monitor-heartbeat', (req, res) => {
    const { pid } = req.body;
    monitorLastHeartbeat = Date.now();
    if (pid) monitorPid = pid;
    res.json({ success: true });
});
```

- [ ] **Step 6: 扩展 session 数据模型**

在 `getOrCreateSession` 函数中，为 session 对象添加新字段：

```javascript
function getOrCreateSession(sessionId, project) {
    if (!sessions[sessionId]) {
        sessions[sessionId] = {
            id: sessionId,
            project: project || 'unknown',
            state: 'idle',
            task: '',
            progress: 0,
            message: '',
            lastUpdate: Date.now(),
            // 新增字段
            model: null,
            context: null,
            branch: null,
            windowHandle: null
        };
    }
    return sessions[sessionId];
}
```

- [ ] **Step 7: 修改 POST /session 端点接收新字段**

```javascript
app.post('/session', (req, res) => {
    const { sessionId, project, state, task, progress, message, windowHandle, model, context, branch } = req.body;
    const sid = sessionId || 'default';
    const session = getOrCreateSession(sid, project);

    // Auto-add to active sessions
    activeSessionIds.add(sid);

    const prevState = session.state;
    if (state) session.state = state;
    if (task !== undefined) session.task = task;
    if (progress !== undefined) session.progress = Math.min(100, Math.max(0, progress));
    if (message !== undefined) session.message = message;
    if (windowHandle !== undefined) session.windowHandle = windowHandle;
    // 新增字段
    if (model !== undefined) session.model = model;
    if (context !== undefined) session.context = context;
    if (branch !== undefined) session.branch = branch;
    session.lastUpdate = Date.now();

    // Add to history when task completes
    if (state === 'complete' && prevState !== 'complete') {
        addToHistory(session);
    }

    res.json({ success: true });
});
```

- [ ] **Step 8: 验证服务器启动**

```bash
cd E:/Code/claude-monitor
node server.js &
sleep 2
curl -s http://localhost:18989/monitor-status
```

Expected: `{"running":false,"pid":null,"starting":false,"lastAlive":0}`

---

## Task 2: hook.js - 添加 Monitor 启动协调逻辑

**Files:**
- Modify: `E:\Code\claude-monitor\hook.js`

- [ ] **Step 1: 添加 getMonitorStatus 函数**

在 `checkMonitorRunning` 函数后添加：

```javascript
async function getMonitorStatus() {
    return new Promise((resolve) => {
        const req = http.request({
            hostname: 'localhost',
            port: PORT,
            path: '/monitor-status',
            method: 'GET',
            timeout: 2000
        }, (res) => {
            let data = '';
            res.on('data', chunk => data += chunk);
            res.on('end', () => {
                try {
                    resolve(JSON.parse(data));
                } catch (e) {
                    resolve({ running: false, pid: null, starting: false });
                }
            });
        });
        req.on('error', () => resolve({ running: false, pid: null, starting: false }));
        req.on('timeout', () => { req.destroy(); resolve({ running: false, pid: null, starting: false }); });
        req.end();
    });
}
```

- [ ] **Step 2: 添加 setMonitorStarting 函数**

```javascript
async function setMonitorStarting() {
    return new Promise((resolve) => {
        const req = http.request({
            hostname: 'localhost',
            port: PORT,
            path: '/monitor-starting',
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            timeout: 2000
        }, (res) => resolve(true));
        req.on('error', () => resolve(false));
        req.write('{}');
        req.end();
    });
}
```

- [ ] **Step 3: 添加 checkProcessExists 函数**

```javascript
async function checkProcessExists(pid) {
    if (!pid) return false;
    try {
        const result = execSync(
            `powershell -Command "Get-Process -Id ${pid} -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id"`,
            { encoding: 'utf8', timeout: 2000 }
        );
        return result.trim() === String(pid);
    } catch (e) {
        return false;
    }
}
```

- [ ] **Step 4: 重写 ensureMonitorRunning 函数**

替换现有的 `ensureMonitorRunning` 函数：

```javascript
async function ensureMonitorRunning() {
    const serverRunning = await checkServerRunning();
    if (!serverRunning) {
        log('Server not running, starting...');
        await startServerAndMonitor();
        return;
    }

    // 获取 Monitor 状态
    const status = await getMonitorStatus();
    log('Monitor status: ' + JSON.stringify(status));

    // 如果正在启动中，等待
    if (status.starting) {
        log('Monitor starting by another session, waiting...');
        await new Promise(r => setTimeout(r, 1000));
        return;
    }

    // 如果 PID 存在，验证进程是否存活
    if (status.pid) {
        const processExists = await checkProcessExists(status.pid);
        if (processExists) {
            log('Monitor already running, pid=' + status.pid);
            return;
        }
        log('Monitor process dead, pid=' + status.pid);
    }

    // 设置启动锁
    log('Setting monitor starting lock...');
    await setMonitorStarting();

    // 启动 Monitor
    startMonitor();
    log('Monitor started');
}
```

- [ ] **Step 5: 删除旧的 checkMonitorRunning 函数**

删除以下函数（已被 `getMonitorStatus` + `checkProcessExists` 替代）：

```javascript
// 删除这个函数
function checkMonitorRunning() {
    // ... 通过 HTTP 检测 Monitor 心跳状态...
}
```

- [ ] **Step 6: 添加 getGitBranch 函数**

在 `startMonitor` 函数后添加：

```javascript
async function getGitBranch(cwd) {
    if (!cwd) return null;
    try {
        const result = execSync('git branch --show-current', {
            cwd: cwd,
            encoding: 'utf8',
            timeout: 2000
        });
        return result.trim() || null;
    } catch (e) {
        return null;
    }
}
```

- [ ] **Step 7: 修改 extractTaskFromInput 函数**

在函数末尾的 return 语句前添加：

```javascript
function extractTaskFromInput(input) {
    // ... 现有逻辑 ...

    // 从 hook input 中提取 model 和 context
    let model = input.model || null;
    let context = null;

    // 计算 context 显示值
    if (input.context_tokens) {
        context = Math.round(input.context_tokens / 1000) + 'k';
    }

    return {
        state: state,
        task: task,
        message: task,
        model: model,
        context: context,
        branch: null  // 需要异步获取，在主流程处理
    };
}
```

- [ ] **Step 8: 修改 main() 函数的 SessionStart 处理**

找到 `if (hookEvent === 'SessionStart')` 块，修改为：

```javascript
// SessionStart 时确保 Monitor 运行
if (hookEvent === 'SessionStart') {
    log('SessionStart event, ensuring monitor...');
    await ensureMonitorRunning();

    // 获取 git branch
    if (input.cwd) {
        status.branch = await getGitBranch(input.cwd);
    }
}
```

- [ ] **Step 9: 修改 sendStatus 调用包含新字段**

确认 sendStatus 调用已包含新字段（已在 status 对象中）：

```javascript
await sendStatus(status);
```

status 对象已包含 model, context, branch 字段。

- [ ] **Step 10: 验证 hook.js 语法**

```bash
node -c E:/Code/claude-monitor/hook.js
```

Expected: No output (syntax OK)

---

## Task 3: Monitor.cs - 添加 PID 报告和 Alt 键窗口激活

**Files:**
- Modify: `E:\Code\claude-monitor\Monitor.cs`

- [ ] **Step 1: 添加 SendStarted 方法**

在 `SendHeartbeat` 方法后添加：

```csharp
private void SendStarted() {
    try {
        var request = (HttpWebRequest)WebRequest.Create("http://localhost:18989/monitor-started");
        request.Proxy = null;
        request.Timeout = 2000;
        request.Method = "POST";
        request.ContentType = "application/json";
        string body = "{\"pid\":" + Process.GetCurrentProcess().Id + "}";
        byte[] data = Encoding.UTF8.GetBytes(body);
        request.ContentLength = data.Length;
        using (var stream = request.GetRequestStream()) {
            stream.Write(data, 0, data.Length);
        }
        using (var response = request.GetResponse()) { }
        Log("Sent started with pid: " + Process.GetCurrentProcess().Id);
    } catch (Exception ex) { Log("SendStarted error: " + ex.Message); }
}
```

- [ ] **Step 2: 修改 SendHeartbeat 方法携带 PID**

```csharp
private void SendHeartbeat(object sender, EventArgs e) {
    try {
        var request = (HttpWebRequest)WebRequest.Create("http://localhost:18989/monitor-heartbeat");
        request.Proxy = null;
        request.Timeout = 2000;
        request.Method = "POST";
        request.ContentType = "application/json";
        string body = "{\"pid\":" + Process.GetCurrentProcess().Id + "}";
        byte[] data = Encoding.UTF8.GetBytes(body);
        request.ContentLength = data.Length;
        using (var stream = request.GetRequestStream()) {
            stream.Write(data, 0, data.Length);
        }
        using (var response = request.GetResponse()) { }
    } catch { }
}
```

- [ ] **Step 3: 添加 keybd_event P/Invoke**

在 `NativeMethods` 类中添加：

```csharp
[DllImport("user32.dll")]
public static extern void keybd_event(byte bVk, byte bScan, int dwFlags, int dwExtraInfo);
```

- [ ] **Step 4: 重写 ForceForegroundWindow 方法**

替换现有的 `ForceForegroundWindow` 方法：

```csharp
private void ForceForegroundWindow(IntPtr hwnd) {
    // 方法1: Alt 键技巧（解决 Windows 前台窗口限制）
    NativeMethods.keybd_event(0x12, 0, 0, 0);  // Alt down
    NativeMethods.keybd_event(0x12, 0, 2, 0);  // Alt up

    // 如果最小化，先恢复
    if (NativeMethods.IsIconic(hwnd)) {
        NativeMethods.ShowWindow(hwnd, 9); // SW_RESTORE
    }

    // 尝试直接切换
    if (NativeMethods.SetForegroundWindow(hwnd)) {
        NativeMethods.SetFocus(hwnd);
        return;
    }

    // 备用: 线程附加方式
    IntPtr foregroundHwnd = NativeMethods.GetForegroundWindow();
    int currentThread = NativeMethods.GetCurrentThreadId();
    int foregroundThread = NativeMethods.GetWindowThreadProcessId(foregroundHwnd, IntPtr.Zero);
    int targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, IntPtr.Zero);

    if (currentThread != targetThread) {
        NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
        NativeMethods.AttachThreadInput(currentThread, targetThread, true);
    }

    NativeMethods.ShowWindow(hwnd, 5); // SW_SHOW
    NativeMethods.SetForegroundWindow(hwnd);
    NativeMethods.SetFocus(hwnd);

    if (currentThread != targetThread) {
        NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
        NativeMethods.AttachThreadInput(currentThread, targetThread, false);
    }
}
```

- [ ] **Step 5: 修改构造函数 - 发送启动通知**

在构造函数末尾，`SendHeartbeat(null, null)` 调用处修改为：

```csharp
// 心跳定时器，每3秒发送一次心跳（从5秒缩短）
heartbeatTimer = new System.Windows.Forms.Timer();
heartbeatTimer.Interval = 3000;
heartbeatTimer.Tick += SendHeartbeat;
heartbeatTimer.Start();

// 立即发送启动通知
SendStarted();
```

- [ ] **Step 6: 编译 Monitor.cs**

```bash
cd E:/Code/claude-monitor
/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe -reference:System.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll Monitor.cs
```

Expected: No errors

---

## Task 4: Monitor.cs - 添加 model/context/branch 信息显示

**Files:**
- Modify: `E:\Code\claude-monitor\Monitor.cs`

- [ ] **Step 1: 扩展 SessionData 类**

```csharp
class SessionData {
    public string id, project, state, task, windowHandle;
    public string model;
    public string context;
    public string branch;
}
```

- [ ] **Step 2: 修改 ParseSessions 方法提取新字段**

在 `ParseSessions` 方法的循环中，添加新字段解析：

```csharp
for (int i = sessionsStart; i < json.Length && depth > 0; i++) {
    if (json[i] == '{') { if (depth == 1) objStart = i; depth++; }
    else if (json[i] == '}') {
        depth--;
        if (depth == 1 && objStart >= 0) {
            string obj = json.Substring(objStart, i - objStart + 1);
            var s = new SessionData();
            s.id = GetJsonString(obj, "id");
            s.project = GetJsonString(obj, "project");
            s.state = GetJsonString(obj, "state");
            s.task = GetJsonString(obj, "task");
            s.windowHandle = GetJsonString(obj, "windowHandle");
            s.model = GetJsonString(obj, "model");
            s.context = GetJsonString(obj, "context");
            s.branch = GetJsonString(obj, "branch");
            list.Add(s);
            objStart = -1;
        }
    }
    else if (json[i] == ']') depth = 0;
}
```

- [ ] **Step 3: 修改 SessionControl 类 - 添加 infoLbl**

在 `SessionControl` 类中添加字段：

```csharp
private Label infoLbl;  // 显示 "glm-5 | 18.6k ctx | main"
```

- [ ] **Step 4: 修改 SessionControl 构造函数**

在构造函数中添加 infoLbl，并调整 taskLbl 位置：

```csharp
public SessionControl() {
    this.Height = 65;
    this.BackColor = Color.FromArgb(36, 36, 56);
    this.Cursor = Cursors.Hand;

    dot = new Label() { Location = new Point(5, 5), Size = new Size(10, 10), BackColor = Color.Gray };
    statusLbl = new Label() { Location = new Point(18, 4), Size = new Size(60, 16), ForeColor = Color.FromArgb(180, 180, 180), BackColor = Color.Transparent, Font = new Font("Segoe UI", 9) };
    projectLbl = new Label() { Location = new Point(80, 4), Size = new Size(215, 16), ForeColor = Color.FromArgb(102, 126, 234), BackColor = Color.Transparent, Font = new Font("Segoe UI", 9, FontStyle.Bold) };

    // 新增信息标签
    infoLbl = new Label();
    infoLbl.Location = new Point(80, 20);
    infoLbl.Size = new Size(215, 14);
    infoLbl.ForeColor = Color.FromArgb(120, 120, 120);
    infoLbl.BackColor = Color.Transparent;
    infoLbl.Font = new Font("Segoe UI", 8);

    taskLbl = new Label();
    taskLbl.Location = new Point(5, 36);  // 位置下移
    taskLbl.Size = new Size(295, 40);
    taskLbl.ForeColor = Color.White;
    taskLbl.BackColor = Color.Transparent;
    taskLbl.Font = new Font("Segoe UI", 9);
    taskLbl.AutoSize = false;

    // ... 现有的 tooltip 和 timer 代码 ...

    // 修改 child controls 数组
    foreach (Control c in new Control[] { dot, statusLbl, projectLbl, infoLbl, taskLbl }) {
        c.MouseClick += (s, e) => { this.OnMouseClick(e); };
        c.Cursor = Cursors.Hand;
    }

    this.Controls.AddRange(new Control[] { dot, statusLbl, projectLbl, infoLbl, taskLbl });
}
```

- [ ] **Step 5: 修改 UpdateData 方法显示信息**

在 `UpdateData` 方法中添加信息显示逻辑：

```csharp
public void UpdateData(SessionData data) {
    _sessionId = data.id ?? "";
    _windowHandle = data.windowHandle ?? "";
    projectLbl.Text = data.project;
    CurrentTask = data.task;
    string taskText = string.IsNullOrEmpty(data.task) ? "..." : data.task;

    // 构建信息行
    var infoParts = new List<string>();
    if (!string.IsNullOrEmpty(data.model)) infoParts.Add(data.model);
    if (!string.IsNullOrEmpty(data.context)) infoParts.Add(data.context + " ctx");
    if (!string.IsNullOrEmpty(data.branch)) infoParts.Add(data.branch);
    infoLbl.Text = string.Join(" | ", infoParts);

    // ... 其余现有逻辑 ...
}
```

- [ ] **Step 6: 修改 _requiredHeight 计算**

调整高度计算以适应新的布局：

```csharp
// 在 UpdateData 方法中
_requiredHeight = 36 + taskHeight + 10; // header(36) + taskHeight + padding(10)
_requiredHeight = Math.Max(_requiredHeight, 60); // 最小高度增加
_requiredHeight = Math.Min(_requiredHeight, 150); // 最大高度增加
```

- [ ] **Step 7: 编译最终版本**

```bash
cd E:/Code/claude-monitor
/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe -reference:System.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll Monitor.cs
```

Expected: No errors

---

## Task 5: 集成测试

- [ ] **Step 1: 重启服务器**

```bash
# 杀死旧进程
taskkill //F //IM node.exe 2>/dev/null || true
taskkill //F //IM Monitor.exe 2>/dev/null || true

# 启动新服务器
cd E:/Code/claude-monitor
node server.js &
sleep 2
```

- [ ] **Step 2: 启动 Monitor**

```bash
cd E:/Code/claude-monitor
start Monitor.exe
sleep 2
tasklist | grep Monitor
curl -s http://localhost:18989/monitor-status
```

Expected: Monitor 进程运行，monitor-status 显示 running=true 和正确 PID

- [ ] **Step 3: 测试多会话启动**

关闭 Monitor 窗口，然后同时启动 2-3 个新的 Claude Code 会话。

Expected: 只有一个 Monitor 窗口保持运行

- [ ] **Step 4: 测试窗口激活**

点击 Monitor 窗口中的会话条目。

Expected: 对应的 Claude Code 窗口被激活并置于前台

- [ ] **Step 5: 测试信息显示**

检查 Monitor 窗口是否显示 model/context/branch 信息。

Expected: 信息行正确显示（如 "glm-5 | 18k ctx | main"）