# Claude Hub 项目踩坑记录

## 一、窗口句柄捕获问题

### 问题1：Windows Terminal 多标签页架构
**现象**：多个 Claude Code 会话运行在不同终端标签页，但 `Process.MainWindowHandle` 只返回一个窗口句柄。

**原因**：Windows Terminal 所有标签页属于同一个进程，每个标签页有独立的 `CASCADIA_HOSTING_WINDOW_CLASS` 窗口，但 `MainWindowHandle` 只返回其中一个。

**错误尝试**：
1. 通过进程树查找（node → bash → conhost → CASCADIA）—— **失败**，CASCADIA 窗口属于 WindowsTerminal 进程，不是 conhost
2. 通过窗口标题匹配 —— **失败**，标题都一样（"? Claude Code"），无法区分
3. 使用 `GetConsoleWindow()` —— **失败**，返回的是 PseudoConsole，不是可见窗口

**正确方案**：
- 在 `SessionStart`（startup/resume）时捕获前台窗口
- 验证窗口类名是否为终端类型（`CASCADIA_HOSTING_WINDOW_CLASS`、`ConsoleWindowClass`、`PseudoConsoleWindow`）
- 将句柄保存到文件，与 sessionId 绑定

### 问题2：UserPromptSubmit 时捕获窗口不准确
**现象**：在 UserPromptSubmit 时捕获的窗口句柄经常不是终端窗口，或者是错误的终端标签页。

**原因**：
1. Hook 执行有延迟，用户可能在模型思考期间切换了窗口
2. **排队消息场景**：用户输入消息时 Claude Code 正在执行，消息排队等待；用户切换窗口后，排队消息自动发送触发 UserPromptSubmit
3. **无法区分正确终端**：Windows Terminal 所有标签页属于同一进程，窗口类名相同，无法区分"正确的终端"和"另一个终端标签页"

**解决方案**：只在 SessionStart 捕获
```javascript
// 窗口句柄唯一可信来源：SessionStart
// - SessionStart (startup)：用户刚启动会话，正在操作正确的终端
// - SessionStart (resume)：用户切换到此会话，正在操作正确的终端
//
// UserPromptSubmit 完全不捕获窗口：
// - 排队消息时用户可能切到另一个终端标签页 → 前台是终端但不是正确终端
// - 无法区分"正确终端"和"另一个终端" → 捕获可能绑定错误窗口
//
// 修正机会：如果绑定错误，等待下次 SessionStart 重新捕获
```

---

## 二、PowerShell 调用问题

### 问题：内联 PowerShell 命令失败
**现象**：复杂的 PowerShell 命令因字符串转义问题而失败。

**错误示例**：
```javascript
const result = execSync(
    `powershell -Command "
Add-Type -TypeDefinition 'using System; ...';
$h = [FG]::GetForegroundWindow();
...
'"`,
    { encoding: 'utf8', timeout: 3000 }
);
```

**问题**：多层嵌套的引号、特殊字符转义极其复杂，容易出错。

**解决方案**：
1. 创建独立的 `.ps1` 脚本文件
2. 编译 C# DLL 供 PowerShell 调用
3. 使用 `execSync('powershell -File script.ps1')` 执行

---

## 三、Monitor 自身最小化问题

### 问题：点击会话时 Monitor 窗口被最小化
**现象**：点击会话切换窗口时，Monitor 自身被最小化而不是目标终端窗口。

**原因**：`FindWindowByTitle("claude")` 匹配到了 "Claude Monitor"。

**解决方案**：
```csharp
// 排除自身窗口
if (hwnd != IntPtr.Zero && hwnd != this.Handle)
{
    // 执行窗口操作
}
```

---

## 四、Win32 API 使用问题

### 问题：keybd_event 已弃用
**现象**：使用 `keybd_event` 导致不稳定的行为。

**解决方案**：使用 `SendInput` 替代：
```csharp
[StructLayout(LayoutKind.Sequential)]
struct INPUT {
    public uint type;
    public InputUnion U;
}

[StructLayout(LayoutKind.Explicit)]
struct InputUnion {
    [FieldOffset(0)] public KEYBDINPUT ki;
}

// 使用 SendInput 发送 Alt 键
```

---

## 五、句柄验证问题

### 问题：保存的句柄可能已失效或不是终端
**现象**：之前保存的窗口句柄可能指向已关闭的窗口，或者不是终端窗口。

**解决方案**：
```javascript
function loadWindowHandle(sessionId) {
    // 1. 检查窗口是否存在 (IsWindow)
    // 2. 检查窗口类名是否是终端类型
    const terminalClasses = ['CASCADIA_HOSTING_WINDOW_CLASS', 'ConsoleWindowClass', 'PseudoConsoleWindow'];
    if (!terminalClasses.includes(className)) {
        return null; // 拒绝非终端窗口
    }
}
```

---

## 六、代码结构问题

### 问题1：函数定义在回调内部
**错误**：
```javascript
process.stdin.on('end', async () => {
    function loadWindowHandle(sessionId) { ... }  // 错误位置
    // ...
});
```

**正确**：在模块作用域定义函数。

### 问题2：常量重复定义
**错误**：
```javascript
function getForegroundWindow() {
    const terminalClasses = ['CASCADIA_HOSTING_WINDOW_CLASS', ...];
}

function loadWindowHandle() {
    const terminalClasses = ['CASCADIA_HOSTING_WINDOW_CLASS', ...];  // 重复
}
```

**正确**：
```javascript
const TERMINAL_WINDOW_CLASSES = ['CASCADIA_HOSTING_WINDOW_CLASS', ...];

function getForegroundWindow() {
    if (TERMINAL_WINDOW_CLASSES.includes(className)) { ... }
}
```

### 问题3：TOCTOU 反模式
**错误**：
```javascript
if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });
```

**正确**：
```javascript
fs.mkdirSync(dir, { recursive: true });  // 不会抛出已存在错误
```

---

## 七、Monitor.cs JSON 解析问题

### 问题：手动字符串解析 JSON
**错误**：
```csharp
int start = json.IndexOf("\"" + sessionId + "\"");
int handleStart = json.IndexOf("\"handle\":", start);
// ... 脆弱的 substring 操作
```

**问题**：格式变化（空格、换行）会导致解析失败。

**解决方案**：使用正则表达式：
```csharp
string pattern = "\"" + Regex.Escape(sessionId) + "\"[^}]*?\"handle\"\\s*:\\s*\"?(\\d+)";
var match = Regex.Match(json, pattern);
```

---

## 八、项目文件清单

**核心文件**：
| 文件 | 作用 |
|------|------|
| `hook.js` | Claude Code Hook，捕获状态和窗口句柄 |
| `server.js` | HTTP 服务器，存储会话状态 |
| `Monitor.cs` / `Monitor.exe` | Windows Forms 监控窗口 |
| `GetForeground.cs` / `GetForeground.dll` | 获取前台窗口信息的 C# DLL |
| `get-foreground-window.ps1` | 调用 DLL 的 PowerShell 脚本 |
| `find-window-by-pid.ps1` | 通过进程树查找窗口（备用） |

**已删除的调试文件**：
- `find-cascadia-windows.ps1`
- `walk-node-tree.ps1`
- `check-bash-tree.ps1`
- `FindTerminal.cs` / `FindTerminal.dll`
- 等

---

## 九、关键经验总结

1. **Windows Terminal 架构决定了窗口句柄必须通过前台窗口捕获**，无法通过进程关系推导
2. **PowerShell 内联命令是陷阱**，应使用独立脚本文件
3. **窗口句柄必须验证有效性**：IsWindow + 类名检查
4. **Hook 执行有延迟**，SessionStart 时捕获比 UserPromptSubmit 更准确
5. **常量提取到模块顶层**，避免重复定义
6. **函数定义在模块作用域**，不要放在回调内部

---

## 十、窗口激活状态检测

### 问题：如何准确判断窗口是否处于激活状态
**背景**：之前的实现只检查 `IsIconic(hwnd)` 判断最小化，但无法检测：
- 窗口是否被其他窗口遮挡
- 窗口是否被系统隐藏（DWM cloaked）
- 窗口是否是前台激活窗口

### 解决方案：多层状态检测

```csharp
enum WindowActivationState
{
    Invalid,           // 窗口句柄无效
    Minimized,         // 最小化
    Hidden,            // 隐藏
    Cloaked,           // 系统隐藏（DWM）
    Foreground,        // 前台激活窗口
    Obscured,          // 被其他窗口遮挡
    VisibleButNotForeground  // 可见但不是前台
}

WindowActivationState GetWindowActivationState(IntPtr hwnd)
{
    // 1. IsWindow() - 检查句柄有效性
    // 2. IsIconic() - 检查最小化
    // 3. IsWindowVisible() - 检查可见性
    // 4. DwmGetWindowAttribute(DWMWA_CLOAKED) - 检查系统隐藏
    // 5. GetForegroundWindow() - 检查是否是前台窗口
    // 6. Z-order 遍历检测遮挡 - GetWindow(GW_HWNDPREV)
}
```

### 关键 Win32 API

| API | 作用 |
|-----|------|
| `IsIconic(hwnd)` | 检查最小化状态 |
| `IsWindowVisible(hwnd)` | 检查可见性（WS_VISIBLE） |
| `GetForegroundWindow()` | 获取前台窗口 |
| `GetWindow(hwnd, GW_HWNDPREV)` | 获取 Z-order 上层窗口 |
| `GetWindowRect(hwnd)` | 获取窗口矩形区域 |
| `DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED)` | 检查 DWM cloaked 状态 |

### 遮挡检测原理

```
目标窗口 ← GetWindow(GW_HWNDPREV) → 上层窗口1 → 上层窗口2 → ...
     ↓                                        ↓
 GetWindowRect()                         GetWindowRect()
     ↓                                        ↓
 检查矩形重叠 → 如果重叠且上层窗口可见 → 判定为被遮挡
```

### 切换行为逻辑

| 当前状态 | 点击行为 |
|----------|----------|
| Minimized/Cloaked/Hidden/Obscured | 激活窗口（恢复并置于前台） |
| Foreground/Visible | 最小化窗口 |
| Invalid | 不执行操作 |

---
## 十一、JSON 解析 Bug - 字符串内容影响花括号计数

### 问题：会话列表只显示第一个会话
**现象**：server 返回 3 个会话，但 Monitor 只显示 1 个。

**日志**：
```
ParseSessions: found ']' at 1142, depth=2
ParseSessions: total parsed=1
```

**原因**：`ParseSessions` 方法没有跟踪字符串状态，导致字符串值中的 `{` 或 `}` 字符被错误地计入花括号深度。当用户的 `userMessage` 或 `task` 字段包含这些字符时，解析器认为对象内部有嵌套结构，导致 depth 计数错误，解析提前终止。

**错误代码**：
```csharp
for (int i = sessionsStart; i < json.Length && depth > 0; i++)
{
    if (json[i] == '{') { depth++; }
    else if (json[i] == '}') { depth--; }
    // 没有处理字符串内部的字符！
}
```

**解决方案**：添加字符串状态跟踪：
```csharp
bool inString = false;
int braceBalance = 0;

for (int i = sessionsStart; i < json.Length && depth > 0; i++)
{
    char c = json[i];

    // 跳过字符串内部的字符（除转义引号）
    if (inString)
    {
        if (c == '\\' && i + 1 < json.Length) i++;
        else if (c == '"') inString = false;
        continue;
    }

    if (c == '"') inString = true;
    else if (c == '{') braceBalance++;
    else if (c == '}') braceBalance--;
    // 只有在字符串外部才计数
}
```

**关键经验**：
- JSON 手动解析必须跟踪字符串状态
- 字符串值可能包含任何字符，包括 `{` 和 `}`
- 转义字符 `\` 需要特殊处理