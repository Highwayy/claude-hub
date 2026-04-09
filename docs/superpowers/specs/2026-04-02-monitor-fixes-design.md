# Claude Monitor 修复与增强设计

## 概述

修复三个问题：
1. 多会话时窗口自动关闭
2. 点击会话激活窗口失效
3. 增加 model/context/branch 信息显示

## 问题1：窗口自动关闭修复

### 根因

多会话 SessionStart hook 同时触发时存在竞争条件：
1. Monitor 启动但心跳还没发送（5秒间隔）
2. 其他会话检测到 `monitor-status.running=false`
3. 启动新 Monitor 实例
4. 新实例因 Mutex 检测到已有实例而立即退出
5. 用户看到窗口"闪一下就关闭"

### 解决方案：服务端协调 + 进程检测 + 启动锁

#### 架构

```
┌─────────────┐     GET /monitor-status      ┌─────────────┐
│  hook.js    │ ───────────────────────────▶ │  server.js  │
│ (多会话)     │     {running, pid, starting} │             │
└─────────────┘ ◀─────────────────────────── └─────────────┘
                          │
        ┌─────────────────┼─────────────────┐
        ▼                 ▼                 ▼
   POST /monitor-   POST /monitor-     POST /monitor-
   starting         started            alive
   (启动前)          (启动后)           (定期心跳)
        │                 │                 │
        └─────────────────┴─────────────────┘
                          │
                    ┌─────┴─────┐
                    │ Monitor   │
                    │ .exe      │
                    └───────────┘
```

#### server.js 变更

新增状态变量：
```javascript
let monitorPid = null;
let monitorStarting = false;
let monitorStartTimeout = null;
let monitorLastAlive = 0;
```

新增端点：

**GET /monitor-status**
```javascript
// 返回 Monitor 状态
{
  running: boolean,      // true 如果 PID 进程存在且心跳正常
  pid: number | null,    // Monitor 进程 PID
  starting: boolean,     // true 如果正在启动中
  lastAlive: number      // 最后心跳时间戳
}
```

**POST /monitor-starting**
```javascript
// 设置启动锁，5秒后自动过期
// 防止多会话同时启动
monitorStarting = true;
monitorStartTimeout = setTimeout(() => { monitorStarting = false; }, 5000);
```

**POST /monitor-started**
```javascript
// Monitor 启动后报告 PID
const { pid } = req.body;
monitorPid = pid;
monitorStarting = false;
if (monitorStartTimeout) clearTimeout(monitorStartTimeout);
```

**POST /monitor-alive**（修改现有心跳）
```javascript
// 增加报告 PID
const { pid } = req.body;
monitorLastAlive = Date.now();
if (pid) monitorPid = pid;
```

#### hook.js 变更

`ensureMonitorRunning()` 新逻辑：

```javascript
async function ensureMonitorRunning() {
    // 1. 检查服务端状态
    const status = await getMonitorStatus();
    
    // 2. 如果正在启动中，等待
    if (status.starting) {
        log('Monitor starting by another session, waiting...');
        await new Promise(r => setTimeout(r, 1000));
        return;
    }
    
    // 3. 如果 PID 存在，验证进程是否存活
    if (status.pid) {
        const processExists = await checkProcessExists(status.pid);
        if (processExists) {
            log('Monitor already running, pid=' + status.pid);
            return;
        }
        log('Monitor process dead, pid=' + status.pid);
    }
    
    // 4. 设置启动锁
    await setMonitorStarting();
    
    // 5. 启动 Monitor
    startMonitor();
}
```

新增辅助函数：

```javascript
async function getMonitorStatus() {
    return new Promise((resolve) => {
        const req = http.request({
            hostname: 'localhost', port: PORT,
            path: '/monitor-status', method: 'GET', timeout: 2000
        }, (res) => {
            let data = '';
            res.on('data', chunk => data += chunk);
            res.on('end', () => {
                try { resolve(JSON.parse(data)); }
                catch (e) { resolve({ running: false }); }
            });
        });
        req.on('error', () => resolve({ running: false }));
        req.on('timeout', () => { req.destroy(); resolve({ running: false }); });
        req.end();
    });
}

async function setMonitorStarting() {
    return new Promise((resolve) => {
        const req = http.request({
            hostname: 'localhost', port: PORT,
            path: '/monitor-starting', method: 'POST', timeout: 2000
        }, (res) => resolve(true));
        req.on('error', () => resolve(false));
        req.write('{}');
        req.end();
    });
}

async function checkProcessExists(pid) {
    try {
        const result = execSync(
            `powershell -Command "Get-Process -Id ${pid} -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id"`,
            { encoding: 'utf8', timeout: 2000 }
        );
        return result.trim() === String(pid);
    } catch { return false; }
}
```

#### Monitor.cs 变更

启动时报告 PID：
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
    } catch { }
}
```

心跳时携带 PID：
```csharp
private void SendHeartbeat(object sender, EventArgs e) {
    try {
        var request = (HttpWebRequest)WebRequest.Create("http://localhost:18989/monitor-alive");
        // ... 同上，发送 {pid: xxx}
    } catch { }
}
```

修改构造函数：
```csharp
// 立即发送启动通知
SendStarted();

// 心跳间隔改为 3 秒
heartbeatTimer.Interval = 3000;
```

---

## 问题2：点击激活窗口失效

### 根因分析

`ForceForegroundWindow` 使用线程附加方式，但 Windows 有严格的窗口切换限制：
- 调用进程必须是前台窗口的所有者
- 线程附加可能因权限问题失败

### 解决方案：Alt 键技巧优先

Windows 有一个已知技巧：按下并释放 Alt 键可以"解锁"前台窗口限制。

**执行顺序**：
1. 先使用 Alt 键技巧
2. 如果最小化，恢复窗口
3. 尝试 SetForegroundWindow
4. 如果失败，回退到线程附加方式

```csharp
[DllImport("user32.dll")]
public static extern void keybd_event(byte bVk, byte bScan, int dwFlags, int dwExtraInfo);

private void ForceForegroundWindow(IntPtr hwnd) {
    // Step 1: Alt 键技巧（解决 Windows 前台窗口限制）
    NativeMethods.keybd_event(0x12, 0, 0, 0);  // Alt down
    NativeMethods.keybd_event(0x12, 0, 2, 0);  // Alt up
    
    // Step 2: 如果最小化，先恢复
    if (NativeMethods.IsIconic(hwnd)) {
        NativeMethods.ShowWindow(hwnd, 9); // SW_RESTORE
    }
    
    // Step 3: 尝试直接切换
    if (NativeMethods.SetForegroundWindow(hwnd)) {
        return; // 成功
    }
    
    // Step 4: 备用 - 线程附加方式
    // ... 现有逻辑
}
```

---

## 问题3：显示模型、上下文、分支信息

### 数据来源

| 字段 | 来源 | 获取方式 |
|------|------|----------|
| Model | hook input | `input.model` (SessionStart 时提供，如 "glm-5") |
| Context | hook input | `input.context_tokens` 或从 `input.context_percentage` 计算 |
| Branch | git 命令 | 在 cwd 目录执行 `git branch --show-current` |

**Context 获取细节**：
- Claude Code hook input 中包含 `context_tokens` 和 `context_window_size`
- 显示格式：`{context_tokens / 1000}k`，如 "18.6k"
- 如果没有 context 数据，显示为空或不显示该项

### UI 布局

```
┌─────────────────────────────────────┐
│ Claude                    [P] [X]   │
├─────────────────────────────────────┤
│ ● Working  Code                     │
│ glm-5 | 18.6k ctx | main            │  ← 新增信息行
│ Reading: hook.js...                 │
└─────────────────────────────────────┘
```

### SessionData 扩展

```csharp
class SessionData {
    public string id, project, state, task, windowHandle;
    public string model;      // 新增: "glm-5"
    public string context;    // 新增: "18.6k"
    public string branch;     // 新增: "main"
}
```

### SessionControl UI 变更

新增信息标签：
```csharp
private Label infoLbl;  // 显示 "glm-5 | 18.6k ctx | main"

public SessionControl() {
    // ... 现有代码
    
    infoLbl = new Label();
    infoLbl.Location = new Point(80, 20);  // projectLbl 下方
    infoLbl.Size = new Size(215, 14);
    infoLbl.ForeColor = Color.FromArgb(120, 120, 120);
    infoLbl.BackColor = Color.Transparent;
    infoLbl.Font = new Font("Segoe UI", 8);
    
    this.Controls.Add(infoLbl);
    // 调整 taskLbl 位置
    taskLbl.Top = 36;
}
```

### hook.js 扩展

`extractTaskFromInput` 返回值增加：

```javascript
function extractTaskFromInput(input) {
    // ... 现有逻辑

    // 从 hook input 中提取 model 和 context
    let model = input.model || null;
    let context = null;

    // 计算 context 显示值
    if (input.context_tokens) {
        context = Math.round(input.context_tokens / 1000) + 'k';
    }

    return {
        state, task, message,
        model: model,
        context: context,
        branch: null  // 需要异步获取，在主流程处理
    };
}
```

SessionStart 时获取 branch：

```javascript
async function getGitBranch(cwd) {
    try {
        const result = execSync('git branch --show-current', {
            cwd: cwd,
            encoding: 'utf8',
            timeout: 2000
        });
        return result.trim() || null;
    } catch { return null; }
}
```

在 `main()` 的 SessionStart 处理中调用：

```javascript
if (hookEvent === 'SessionStart') {
    log('SessionStart event, ensuring monitor...');
    await ensureMonitorRunning();

    // 获取 git branch
    if (input.cwd) {
        status.branch = await getGitBranch(input.cwd);
    }
}
```

### server.js 数据存储

session 对象扩展：
```javascript
sessions[sessionId] = {
    id, project, state, task, progress, message, windowHandle,
    model: null,
    context: null,
    branch: null,
    lastUpdate: Date.now()
};
```

---

## 文件变更清单

| 文件 | 变更内容 |
|------|----------|
| server.js | 新增端点、状态变量、session 字段 |
| hook.js | 新增启动协调逻辑、git branch 获取 |
| Monitor.cs | 新增 PID 报告、Alt 键技巧、UI 扩展 |

---

## 测试要点

1. **多会话启动**：关闭 Monitor 后，同时启动 2-3 个新会话，验证只有一个 Monitor 窗口保持
2. **进程检测**：手动杀死 Monitor 进程后启动新会话，验证能正确重启
3. **窗口激活**：点击会话条目，验证对应 Claude Code 窗口被激活
4. **信息显示**：验证 model/context/branch 正确显示在窗口中