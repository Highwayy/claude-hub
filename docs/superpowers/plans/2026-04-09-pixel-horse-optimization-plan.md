# Claude Monitor 像素风格优化 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 Claude Monitor 界面优化为像素风格，引入像素马状态指示器、优化进度条、改进布局和交互。

**Architecture:** 在现有 WinForms SessionControl 上叠加像素马绘制、修改进度条样式、调整布局参数。保持原有功能不变，仅增强视觉表现。

**Tech Stack:** C# WinForms, Node.js Express, PowerShell

---

## 文件结构

| 文件 | 变更类型 | 负责内容 |
|------|----------|----------|
| `claude-monitor-release/Monitor.cs` | 修改 | 像素马绘制、进度条样式、布局、按钮、交互、拖动排序 |
| `claude-monitor-release/server.js` | 修改 | 增加 contextPercentage 字段 |
| `claude-monitor-release/hook.js` | 修改 | 提取 context 百分比数据 |

---

## Task 1: server.js - 增加 contextPercentage 字段

**Files:**
- Modify: `E:\Code\claude-monitor-release\server.js:62-74`

- [ ] **Step 1: 修改 getOrCreateSession 函数，增加 contextPercentage 字段**

打开 `server.js`，找到 `getOrCreateSession` 函数（约第62行），在 session 对象中增加 `contextPercentage` 字段：

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
            contextPercentage: 0,  // 新增：上下文使用百分比
            lastUpdate: Date.now()
        };
    }
    return sessions[sessionId];
}
```

- [ ] **Step 2: 修改 POST /session 端点，接收 contextPercentage**

找到 `app.post('/session', ...)` 函数（约第151行），在接收参数处增加 contextPercentage：

```javascript
app.post('/session', (req, res) => {
    const { sessionId, project, state, task, progress, message, windowHandle, contextPercentage } = req.body;
    const sid = sessionId || 'default';
    const session = getOrCreateSession(sid, project);

    // ... existing code ...

    if (contextPercentage !== undefined) session.contextPercentage = Math.min(100, Math.max(0, contextPercentage));
    session.lastUpdate = Date.now();

    // ... rest of function ...
});
```

- [ ] **Step 3: 修改 GET /status 端点，返回 contextPercentage**

找到 `app.get('/status', ...)` 函数（约第97行），确保 session 对象在返回时包含 contextPercentage（sessionList 已包含全部字段，无需额外修改，但需验证单会话响应也包含）：

```javascript
// 在单会话分支（约第111-117行），确保返回 sessions 字段
} else if (sessionList.length === 1) {
    const s = sessionList[0];
    response.state = s.state;
    response.task = s.task;
    response.progress = s.progress;
    response.message = s.message;
    response.sessions = sessionList;  // 已包含 contextPercentage
}
```

- [ ] **Step 4: 测试 server.js 启动**

```bash
cd E:/Code/claude-monitor-release
node server.js
```

访问 http://localhost:18989/status 验证返回正常。

- [ ] **Step 5: Commit server.js**

```bash
git add server.js
git commit -m "feat(server): add contextPercentage field for progress bar color"
```

---

## Task 2: hook.js - 提取 context 百分比数据

**Files:**
- Modify: `E:\Code\claude-monitor-release\hook.js:177-288`

- [ ] **Step 1: 修改 extractTaskFromInput 函数，计算 contextPercentage**

找到 `extractTaskFromInput` 函数（约第177行），在函数开头增加 context 百分比计算：

```javascript
function extractTaskFromInput(input) {
    const toolName = input.tool_name || '';
    const toolInput = input.tool_input || {};
    const hookEvent = input.hook_event_name || '';

    log('hook event: ' + hookEvent + ', tool: ' + toolName);

    // 计算 context 百分比
    let contextPercentage = 0;
    if (input.context_tokens && input.context_window_size) {
        contextPercentage = Math.round(
            (input.context_tokens / input.context_window_size) * 100
        );
    }

    // ... existing switch cases ...

    // 修改返回值，增加 contextPercentage
    return {
        state: state,
        task: task,
        message: task,
        contextPercentage: contextPercentage
    };
}
```

- [ ] **Step 2: 修改所有返回语句，包含 contextPercentage**

逐一修改函数内的所有 `return` 语句，确保每个都包含 `contextPercentage`：

**UserPromptSubmit 分支（约第185-189行）**：
```javascript
if (hookEvent === 'UserPromptSubmit' && input.user_prompt) {
    return {
        state: 'working',
        task: truncate(input.user_prompt, 100),
        message: 'Processing user request...',
        contextPercentage: contextPercentage
    };
}
```

**Stop 分支（约第192-208行）**：
```javascript
if (hookEvent === 'Stop') {
    // ... existing code ...
    return {
        state: 'complete',
        task: taskText,
        message: 'Ready',
        contextPercentage: contextPercentage
    };
}
```

**SessionStart 分支（约第211-216行）**：
```javascript
if (hookEvent === 'SessionStart') {
    return {
        state: 'idle',
        task: 'Session started',
        message: 'Ready',
        contextPercentage: contextPercentage
    };
}
```

- [ ] **Step 3: 测试 hook.js 发送 contextPercentage**

手动测试：
```bash
cd E:/Code/claude-monitor-release
node hook.js working "Test task"
```

检查 http://localhost:18989/status 返回是否包含 contextPercentage。

- [ ] **Step 4: Commit hook.js**

```bash
git add hook.js
git commit -m "feat(hook): extract contextPercentage from Claude input"
```

---

## Task 3: Monitor.cs - 像素马 Sprite 类

**Files:**
- Modify: `E:\Code\claude-monitor-release\Monitor.cs:552-770`（在 NativeMethods 类之后新增）

- [ ] **Step 1: 创建 PixelHorseSprite 类框架**

在 `NativeMethods` 类之后（约第770行），添加新的 `PixelHorseSprite` 类：

```csharp
class PixelHorseSprite
{
    private Bitmap[] frames;
    private int currentFrame = 0;
    private Color horseColor;
    private Color outlineColor = Color.FromArgb(100, 0, 0, 0);
    private string state;

    public PixelHorseSprite(string state)
    {
        this.state = state;
        this.horseColor = GetStateColor(state);
        GenerateFrames();
    }

    private Color GetStateColor(string state)
    {
        switch (state)
        {
            case "thinking": return Color.FromArgb(93, 173, 226);  // #5dade2
            case "working": return Color.FromArgb(46, 204, 113);  // #2ecc71
            case "error": return Color.FromArgb(231, 76, 60);     // #e74c3c
            case "waiting": return Color.FromArgb(243, 156, 18);  // #f39c12
            case "complete": return Color.FromArgb(46, 204, 113); // #2ecc71
            default: return Color.FromArgb(149, 165, 166);        // #95a5a6 (idle)
        }
    }

    private void GenerateFrames()
    {
        // 根据状态生成不同帧数
        int frameCount = state == "working" ? 3 : (state == "thinking" ? 4 : 1);
        frames = new Bitmap[frameCount];

        for (int i = 0; i < frameCount; i++)
        {
            frames[i] = GenerateFrame(i);
        }
    }

    private Bitmap GenerateFrame(int frameIndex)
    {
        // 8x8 像素马，放大到 16x16 显示
        int scale = 2;
        Bitmap bmp = new Bitmap(16, 16);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);

            // 像素马基础形状（每像素2x2实际像素）
            int[][] horsePixels = GetHorsePixels(frameIndex);

            foreach (int[] pixel in horsePixels)
            {
                int x = pixel[0] * scale;
                int y = pixel[1] * scale;

                // 绘制马身体
                g.FillRectangle(new SolidBrush(horseColor), x, y, scale, scale);

                // 绘制深色描边（只在外边缘）
                if (IsOutlinePixel(pixel, horsePixels))
                {
                    g.FillRectangle(new SolidBrush(outlineColor), x, y, scale, scale);
                    g.FillRectangle(new SolidBrush(horseColor), x + 1, y + 1, scale - 2, scale - 2);
                }
            }

            // 绘制眼睛/表情
            DrawExpression(g, frameIndex, scale);
        }
        return bmp;
    }

    private int[][] GetHorsePixels(int frameIndex)
    {
        // Working 状态奔跑动画
        if (state == "working")
        {
            switch (frameIndex)
            {
                case 0: return new int[][] { // Frame 1 - 左腿前
                    new[] {2,1}, new[] {3,1}, new[] {4,1}, new[] {5,1}, // 头部
                    new[] {3,2}, new[] {4,2}, // 颈部
                    new[] {3,3}, new[] {4,3}, new[] {5,3}, // 身体上
                    new[] {3,4}, new[] {4,4}, new[] {5,4}, new[] {6,4}, // 身体下
                    new[] {2,5}, new[] {5,5}, // 左前腿, 右前腿
                    new[] {3,6}, new[] {6,6}, // 左后腿, 右后腿
                };
                case 1: return new int[][] { // Frame 2 - 双腿并拢
                    new[] {2,1}, new[] {3,1}, new[] {4,1}, new[] {5,1},
                    new[] {3,2}, new[] {4,2},
                    new[] {3,3}, new[] {4,3}, new[] {5,3},
                    new[] {3,4}, new[] {4,4}, new[] {5,4}, new[] {6,4},
                    new[] {3,5}, new[] {6,5},
                    new[] {3,6}, new[] {6,6},
                };
                case 2: return new int[][] { // Frame 3 - 右腿前
                    new[] {2,1}, new[] {3,1}, new[] {4,1}, new[] {5,1},
                    new[] {3,2}, new[] {4,2},
                    new[] {3,3}, new[] {4,3}, new[] {5,3},
                    new[] {3,4}, new[] {4,4}, new[] {5,4}, new[] {6,4},
                    new[] {3,5}, new[] {6,5},
                    new[] {2,6}, new[] {5,6},
                };
            }
        }

        // Thinking 状态旋转动画（4帧，360度）
        if (state == "thinking")
        {
            // 固定马形状，通过眼睛位置的↻符号表示思考
            return new int[][] {
                new[] {2,1}, new[] {3,1}, new[] {4,1}, new[] {5,1},
                new[] {3,2}, new[] {4,2},
                new[] {3,3}, new[] {4,3}, new[] {5,3},
                new[] {3,4}, new[] {4,4}, new[] {5,4}, new[] {6,4},
                new[] {3,5}, new[] {6,5},
                new[] {3,6}, new[] {6,6},
            };
        }

        // 其他状态静止
        return new int[][] {
            new[] {2,1}, new[] {3,1}, new[] {4,1}, new[] {5,1},
            new[] {3,2}, new[] {4,2},
            new[] {3,3}, new[] {4,3}, new[] {5,3},
            new[] {3,4}, new[] {4,4}, new[] {5,4}, new[] {6,4},
            new[] {3,5}, new[] {6,5},
            new[] {3,6}, new[] {6,6},
        };
    }

    private bool IsOutlinePixel(int[] pixel, int[][] allPixels)
    {
        // 简化：所有像素都加描边
        return true;
    }

    private void DrawExpression(Graphics g, int frameIndex, int scale)
    {
        // Thinking: 绘制 ↻ 符号在头部位置
        if (state == "thinking")
        {
            using (Font font = new Font("Arial", 6, FontStyle.Bold))
            {
                g.DrawString("↻", font, new SolidBrush(Color.White), 4 * scale - 2, 1 * scale - 1);
            }
        }

        // Error: 绘制皱眉符号
        if (state == "error")
        {
            using (Font font = new Font("Arial", 6, FontStyle.Bold))
            {
                g.DrawString("×", font, new SolidBrush(Color.White), 4 * scale - 2, 1 * scale - 1);
            }
        }

        // Idle: 绘制静止符号 —
        if (state == "idle")
        {
            using (Font font = new Font("Arial", 6, FontStyle.Regular))
            {
                g.DrawString("—", font, new SolidBrush(Color.White), 4 * scale - 2, 1 * scale - 1);
            }
        }

        // Working/Complete: 绘制眼睛 ○
        if (state == "working" || state == "complete")
        {
            using (Font font = new Font("Arial", 5, FontStyle.Regular))
            {
                g.DrawString("○", font, new SolidBrush(Color.White), 4 * scale - 1, 1 * scale - 1);
            }
        }

        // Waiting: 绘制等待符号 ⏳
        if (state == "waiting")
        {
            using (Font font = new Font("Arial", 6, FontStyle.Bold))
            {
                g.DrawString("⏳", font, new SolidBrush(Color.White), 4 * scale - 2, 1 * scale - 1);
            }
        }
    }

    public Bitmap GetCurrentFrame()
    {
        if (frames == null || frames.Length == 0) return null;
        return frames[currentFrame];
    }

    public void AdvanceFrame()
    {
        if (frames == null || frames.Length <= 1) return;
        currentFrame = (currentFrame + 1) % frames.Length;
    }

    public void UpdateState(string newState)
    {
        if (this.state != newState)
        {
            this.state = newState;
            this.horseColor = GetStateColor(newState);
            currentFrame = 0;
            GenerateFrames();
        }
    }
}
```

- [ ] **Step 2: 编译验证 PixelHorseSprite 类**

在 Monitor.cs 顶部确保引用：
```csharp
using System.Drawing;
using System.Drawing.Drawing2D;
```

编译检查：
```bash
cd E:/Code/claude-monitor-release
# 使用 csc 或 Visual Studio 编译
```

- [ ] **Step 3: Commit PixelHorseSprite 类**

```bash
git add Monitor.cs
git commit -m "feat(Monitor): add PixelHorseSprite class for pixel art horse"
```

---

## Task 4: Monitor.cs - SessionControl 布局重构

**Files:**
- Modify: `E:\Code\claude-monitor-release\Monitor.cs:557-614`

- [ ] **Step 1: 修改 SessionControl 构造函数，调整布局参数**

找到 `SessionControl` 类构造函数（约第576行），修改布局：

```csharp
public SessionControl()
{
    this.Height = 70;  // 从 65 增加，容纳进度条
    this.BackColor = Color.FromArgb(36, 36, 56);
    this.Cursor = Cursors.Hand;
    this.Padding = new Padding(12, 12, 12, 12);  // 新增：内边距 12px

    // 移除 dot Label，改为像素马绘制
    // dot = new Label() { ... }; // 删除

    // 像素马绘制区域（通过 Paint 事件绘制）
    horseSprite = null;  // 在 UpdateData 时初始化

    statusLbl = new Label() {
        Location = new Point(22, 8),  // 从 (18,4) 调整，为像素马留空间
        Size = new Size(70, 18),      // 从 16 增加到 18
        ForeColor = Color.FromArgb(180, 180, 180),
        BackColor = Color.Transparent,
        Font = new Font("Segoe UI", 12, FontStyle.Bold)  // 从 9pt 增加到 12pt，加粗
    };

    projectLbl = new Label() {
        Location = new Point(95, 8),  // 从 (80,4) 调整
        Size = new Size(200, 18),
        ForeColor = Color.FromArgb(102, 126, 234),
        BackColor = Color.Transparent,
        Font = new Font("Segoe UI", 10, FontStyle.Bold)  // 从 9 调整
    };

    taskLbl = new Label();
    taskLbl.Location = new Point(12, 28);  // 从 (5,22) 调整，考虑 padding
    taskLbl.Size = new Size(280, 40);      // 从 295 调整
    taskLbl.ForeColor = Color.White;
    taskLbl.BackColor = Color.Transparent;
    taskLbl.Font = new Font("Segoe UI", 9);
    taskLbl.AutoSize = false;

    // 进度条区域（通过 Paint 绘制）
    progressBarTop = 70;  // 进度条位置在 UpdateData 中动态计算

    toolTip = new ToolTip();
    toolTip.InitialDelay = 500;
    toolTip.ShowAlways = true;

    // ... rest of existing code (flashTimer, waitingTimer, etc.) ...

    // 移除 dot 的 Controls.Add，只保留 statusLbl, projectLbl, taskLbl
    this.Controls.AddRange(new Control[] { statusLbl, projectLbl, taskLbl });
}
```

- [ ] **Step 2: 增加 SessionControl 成员变量**

在 `SessionControl` 类开头（约第557行），增加成员变量：

```csharp
class SessionControl : Panel
{
    private Label statusLbl, projectLbl, taskLbl;
    private PixelHorseSprite horseSprite;  // 新增：像素马
    private int progressBarValue = 0;       // 新增：进度条值
    private int contextPercentage = 0;      // 新增：上下文百分比
    private int progressBarTop = 50;        // 新增：进度条位置
    private bool isHovered = false;         // 新增：hover 状态
    private bool isClicked = false;         // 新增：click 状态

    // ... existing members (flashTimer, waitingTimer, etc.) ...
```

- [ ] **Step 3: Commit 布局调整**

```bash
git add Monitor.cs
git commit -m "feat(Monitor): adjust SessionControl layout with 12px padding"
```

---

## Task 5: Monitor.cs - SessionControl Paint 绘制像素马和进度条

**Files:**
- Modify: `E:\Code\claude-monitor-release\Monitor.cs:614-730`

- [ ] **Step 1: 重写 OnPaint 方法，绘制像素马、进度条、分割线**

在 `SessionControl` 类中，添加 `OnPaint` override：

```csharp
protected override void OnPaint(PaintEventArgs e)
{
    base.OnPaint(e);

    Graphics g = e.Graphics;
    g.SmoothingMode = SmoothingMode.None;  // 像素风格，不平滑

    // 1. 绘制背景（考虑 hover/click 状态）
    Color bgColor = this.BackColor;
    if (isClicked) bgColor = Color.FromArgb(32, 32, 52);
    else if (isHovered) bgColor = Color.FromArgb(42, 42, 62);
    using (SolidBrush bgBrush = new SolidBrush(bgColor))
    {
        g.FillRectangle(bgBrush, this.ClientRectangle);
    }

    // 2. 绘制像素马
    if (horseSprite != null)
    {
        Bitmap horseBmp = horseSprite.GetCurrentFrame();
        if (horseBmp != null)
        {
            g.DrawImage(horseBmp, 12, 8);  // 位置考虑 padding
        }
    }

    // 3. 绘制进度条（10px 高度）
    int barY = this.Height - 15;  // 底部位置
    int barWidth = this.Width - 24;  // 考虑 padding
    int barX = 12;

    // 进度条背景
    using (SolidBrush bgBarBrush = new SolidBrush(Color.FromArgb(40, 40, 40)))
    {
        g.FillRectangle(bgBarBrush, barX, barY, barWidth, 10);
    }

    // 进度条填充（根据 contextPercentage 变色）
    int fillWidth = (int)(barWidth * contextPercentage / 100.0);
    Color progressColor = GetProgressColor(contextPercentage);
    using (LinearGradientBrush progressBrush = new LinearGradientBrush(
        new Point(barX, barY), new Point(barX + fillWidth, barY),
        progressColor, AdjustColor(progressColor, -20)))
    {
        if (fillWidth > 0)
        {
            g.FillRectangle(progressBrush, barX, barY, fillWidth, 10);
        }
    }

    // 进度条百分比文字（右对齐）
    string pctText = contextPercentage + "%";
    using (Font pctFont = new Font("Segoe UI", 8, FontStyle.Bold))
    using (SolidBrush pctBrush = new SolidBrush(Color.FromArgb(150, 150, 150)))
    {
        SizeF textSize = g.MeasureString(pctText, pctFont);
        g.DrawString(pctText, pctFont, pctBrush,
            barX + barWidth - textSize.Width - 2, barY + 1);
    }

    // 4. 绘制分割线（底部）
    using (Pen sepPen = new Pen(Color.FromArgb(20, 255, 255, 255)))
    {
        g.DrawLine(sepPen, 0, this.Height - 1, this.Width, this.Height - 1);
    }

    // 5. 绘制深色边框（像素马描边）
    if (horseSprite != null)
    {
        using (Pen outlinePen = new Pen(Color.FromArgb(80, 0, 0, 0), 1))
        {
            g.DrawRectangle(outlinePen, 11, 7, 18, 18);
        }
    }
}

private Color GetProgressColor(int percentage)
{
    if (percentage < 70) return Color.FromArgb(46, 204, 113);   // #2ecc71 绿色
    if (percentage < 90) return Color.FromArgb(243, 156, 18);  // #f39c12 黄色
    return Color.FromArgb(231, 76, 60);                         // #e74c3c 红色
}

private Color AdjustColor(Color c, int delta)
{
    return Color.FromArgb(
        Math.Max(0, Math.Min(255, c.R + delta)),
        Math.Max(0, Math.Min(255, c.G + delta)),
        Math.Max(0, Math.Min(255, c.B + delta))
    );
}
```

- [ ] **Step 2: 增加 hover 和 click 状态处理**

在 `SessionControl` 类中，增加鼠标事件处理：

```csharp
protected override void OnMouseEnter(EventArgs e)
{
    base.OnMouseEnter(e);
    isHovered = true;
    this.Invalidate();  // 触发重绘
}

protected override void OnMouseLeave(EventArgs e)
{
    base.OnMouseLeave(e);
    isHovered = false;
    isClicked = false;
    this.Invalidate();
}

protected override void OnMouseDown(MouseEventArgs e)
{
    base.OnMouseDown(e);
    if (e.Button == MouseButtons.Left)
    {
        isClicked = true;
        this.Invalidate();
    }
}

protected override void OnMouseUp(MouseEventArgs e)
{
    base.OnMouseUp(e);
    isClicked = false;
    this.Invalidate();
}
```

- [ ] **Step 3: 修改 UpdateData 方法，初始化像素马和进度条**

找到 `UpdateData` 方法（约第658行），修改：

```csharp
public void UpdateData(SessionData data)
{
    _sessionId = data.id ?? "";
    _windowHandle = data.windowHandle ?? "";
    projectLbl.Text = data.project;
    CurrentTask = data.task;

    // 更新像素马状态
    if (horseSprite == null)
    {
        horseSprite = new PixelHorseSprite(data.state);
    }
    else
    {
        horseSprite.UpdateState(data.state);
    }

    // 更新进度条值（从 SessionData 获取）
    // 需要扩展 SessionData 类增加 contextPercentage 字段
    contextPercentage = data.contextPercentage;  // 新增字段

    // ... existing task text handling ...

    // 重新计算高度（包含进度条）
    int taskHeight = CalculateTextHeight(taskText, 280);
    _requiredHeight = 28 + taskHeight + 20 + 15;  // header + task + progress + padding
    _requiredHeight = Math.Max(_requiredHeight, 70);
    _requiredHeight = Math.Min(_requiredHeight, 140);

    this.Height = _requiredHeight;

    // ... existing state handling (waiting, complete, etc.) ...

    // 触发重绘
    this.Invalidate();
}
```

- [ ] **Step 4: 扩展 SessionData 类**

找到 `SessionData` 类（约第552行），增加字段：

```csharp
class SessionData
{
    public string id, project, state, task, windowHandle;
    public int contextPercentage;  // 新增
}
```

- [ ] **Step 5: 修改 ParseSessions 解析 contextPercentage**

找到 `ParseSessions` 方法（约第265行），增加解析：

```csharp
private List<SessionData> ParseSessions(string json)
{
    var list = new List<SessionData>();
    // ... existing parsing code ...

    // 在解析每个 session 对象时增加：
    s.contextPercentage = GetJsonInt(obj, "contextPercentage");

    // ... rest of method ...
}

// 新增辅助方法
private int GetJsonInt(string json, string key)
{
    string search = "\"" + key + "\":";
    int start = json.IndexOf(search);
    if (start < 0) return 0;
    start += search.Length;
    // Skip whitespace
    while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
    if (start >= json.Length) return 0;

    // Parse integer
    int end = start;
    while (end < json.Length && (json[end] >= '0' && json[end] <= '9')) end++;
    if (end > start)
    {
        try { return int.Parse(json.Substring(start, end - start)); }
        catch { return 0; }
    }
    return 0;
}
```

- [ ] **Step 6: Commit Paint 和交互**

```bash
git add Monitor.cs
git commit -m "feat(Monitor): add pixel horse and progress bar painting in SessionControl"
```

---

## Task 6: Monitor.cs - 像素马动画

**Files:**
- Modify: `E:\Code\claude-monitor-release\Monitor.cs:639-656`

- [ ] **Step 1: 修改 Animate 方法，推进像素马帧**

找到 `Animate` 方法（约第639行），修改：

```csharp
public void Animate()
{
    // 推进像素马动画帧
    if (horseSprite != null && (lastState == "thinking" || lastState == "working"))
    {
        horseSprite.AdvanceFrame();
        this.Invalidate();  // 触发重绘
    }

    // 保留原有的脉冲动画（用于 waiting 状态背景闪烁）
    if (lastState == "waiting")
    {
        pulsePhase += 0.2f;
        float alpha = (float)(0.5 + 0.5 * Math.Sin(pulsePhase));
        this.BackColor = Color.FromArgb(
            (int)(36 + 44 * alpha),
            (int)(36),
            (int)(56)
        );
    }
}
```

- [ ] **Step 2: Commit 动画修改**

```bash
git add Monitor.cs
git commit -m "feat(Monitor): add pixel horse frame animation"
```

---

## Task 7: Monitor.cs - 标题栏按钮样式

**Files:**
- Modify: `E:\Code\claude-monitor-release\Monitor.cs:59-86`

- [ ] **Step 1: 修改标题栏按钮为图标样式**

找到 header 按钮创建部分（约第59-86行），修改：

```csharp
// 修改 pinBtn
pinBtn = new Button();
pinBtn.Text = "📌";  // 图标化
pinBtn.Width = 24;
pinBtn.Height = 24;
pinBtn.Dock = DockStyle.Right;
pinBtn.FlatStyle = FlatStyle.Flat;
pinBtn.BackColor = Color.FromArgb(60, 60, 80);  // 透明背景色
pinBtn.ForeColor = Color.White;
pinBtn.Font = new Font("Segoe UI Symbol", 10);
pinBtn.Cursor = Cursors.Hand;
pinBtn.FlatAppearance.BorderSize = 0;
pinBtn.Margin = new Padding(2);
// Tooltip
ToolTip pinTip = new ToolTip();
pinTip.SetToolTip(pinBtn, "钉在顶层");
pinBtn.Click += (s, e) => {
    this.TopMost = !this.TopMost;
    pinBtn.BackColor = this.TopMost ? Color.FromArgb(46, 204, 113) : Color.FromArgb(60, 60, 80);
};
// Hover 效果
pinBtn.MouseEnter += (s, e) => {
    pinBtn.BackColor = Color.FromArgb(80, 80, 100);
};
pinBtn.MouseLeave += (s, e) => {
    pinBtn.BackColor = this.TopMost ? Color.FromArgb(46, 204, 113) : Color.FromArgb(60, 60, 80);
};

// 修改 closeBtn
Button closeBtn = new Button();
closeBtn.Text = "✕";  // 图标化
closeBtn.Width = 24;
closeBtn.Height = 24;
closeBtn.Dock = DockStyle.Right;
closeBtn.FlatStyle = FlatStyle.Flat;
closeBtn.BackColor = Color.FromArgb(60, 60, 80);
closeBtn.ForeColor = Color.White;
closeBtn.Font = new Font("Segoe UI Symbol", 11);
closeBtn.Cursor = Cursors.Hand;
closeBtn.FlatAppearance.BorderSize = 0;
closeBtn.Margin = new Padding(2);
// Tooltip
ToolTip closeTip = new ToolTip();
closeTip.SetToolTip(closeBtn, "关闭会话");
closeBtn.Click += (s, e) => {
    timer.Stop();
    animationTimer.Stop();
    SaveWindowPosition();
    this.Close();
};
// Hover 效果
closeBtn.MouseEnter += (s, e) => {
    closeBtn.BackColor = Color.FromArgb(231, 76, 60);
};
closeBtn.MouseLeave += (s, e) => {
    closeBtn.BackColor = Color.FromArgb(60, 60, 80);
};
```

- [ ] **Step 2: Commit 按钮样式**

```bash
git add Monitor.cs
git commit -m "feat(Monitor): update header buttons with icon style and tooltips"
```

---

## Task 8: Monitor.cs - 状态文字颜色强化

**Files:**
- Modify: `E:\Code\claude-monitor-release\Monitor.cs:709-729`

- [ ] **Step 1: 修改 UpdateData 中的状态文字颜色**

找到状态文字设置部分（约第709行），修改：

```csharp
// 状态文字颜色映射
Color stateColor;
string stateText;

if (data.state == "idle") {
    stateColor = Color.FromArgb(149, 165, 166);  // #95a5a6
    stateText = "Idle";
}
else if (data.state == "waiting") {
    stateColor = Color.FromArgb(243, 156, 18);   // #f39c12
    stateText = "Waiting";
}
else if (data.state == "thinking") {
    stateColor = Color.FromArgb(93, 173, 226);   // #5dade2
    stateText = "Thinking";
}
else if (data.state == "working") {
    stateColor = Color.FromArgb(46, 204, 113);   // #2ecc71
    stateText = "Working";
}
else if (data.state == "complete") {
    stateColor = Color.FromArgb(46, 204, 113);   // #2ecc71
    stateText = "Done";
    // 任务完成文字也变色
    taskLbl.Text = "✓ " + taskText;
    taskLbl.ForeColor = stateColor;
}
else if (data.state == "error") {
    stateColor = Color.FromArgb(231, 76, 60);    // #e74c3c
    stateText = "Error";
}
else {
    stateColor = Color.FromArgb(149, 165, 166);
    stateText = data.state;
}

// 应用颜色
statusLbl.ForeColor = stateColor;
statusLbl.Text = stateText;

// 非 complete 状态重置 taskLbl 颜色
if (data.state != "complete") {
    taskLbl.ForeColor = Color.White;
}
```

- [ ] **Step 2: Commit 状态颜色**

```bash
git add Monitor.cs
git commit -m "feat(Monitor): apply state-specific colors to status text"
```

---

## Task 9: Monitor.cs - 拖动卡片排序（可选功能）

**Files:**
- Modify: `E:\Code\claude-monitor-release\Monitor.cs:557-730`

- [ ] **Step 1: 增加 SessionControl 拖动成员变量**

在 `SessionControl` 类开头增加：

```csharp
private bool isDragging = false;
private int dragStartY = 0;
private int dragOffsetY = 0;
```

- [ ] **Step 2: 实现拖动检测逻辑**

修改 `OnMouseDown`：

```csharp
protected override void OnMouseDown(MouseEventArgs e)
{
    base.OnMouseDown(e);
    if (e.Button == MouseButtons.Left)
    {
        isClicked = true;

        // 检测是否在状态行区域（Y < 28），允许拖动
        if (e.Y < 28)
        {
            isDragging = true;
            dragStartY = e.Y;
            this.BackColor = Color.FromArgb(40, 40, 60);
        }

        this.Invalidate();
    }
}
```

增加 `OnMouseMove`：

```csharp
protected override void OnMouseMove(MouseEventArgs e)
{
    base.OnMouseMove(e);

    if (isDragging)
    {
        dragOffsetY = e.Y - dragStartY;
        this.Top += dragOffsetY;
        dragStartY = e.Y;

        // 检测与其他 session 的交换位置
        CheckDragSwap();
    }
    else if (!isClicked)
    {
        // hover 状态
        isHovered = true;
        this.Invalidate();
    }
}
```

增加 `OnMouseUp`：

```csharp
protected override void OnMouseUp(MouseEventArgs e)
{
    base.OnMouseUp(e);

    if (isDragging)
    {
        isDragging = false;
        this.BackColor = Color.FromArgb(36, 36, 56);

        // 触发父容器重新布局
        if (this.Parent != null)
        {
            ClaudeMonitor parent = this.Parent.FindForm() as ClaudeMonitor;
            if (parent != null)
            {
                parent.ReorderSessions();
            }
        }
    }

    isClicked = false;
    this.Invalidate();
}
```

- [ ] **Step 3: 增加 CheckDragSwap 方法**

```csharp
private void CheckDragSwap()
{
    if (this.Parent == null) return;

    foreach (Control c in this.Parent.Controls)
    {
        if (c is SessionControl other && other != this)
        {
            // 检测是否需要交换位置
            if (this.Top < other.Top && this.Bottom > other.Top + other.Height / 2)
            {
                // 向上拖动，交换位置
                SwapPosition(other);
            }
            else if (this.Top > other.Top && this.Top < other.Top + other.Height / 2)
            {
                // 向下拖动，交换位置
                SwapPosition(other);
            }
        }
    }
}

private void SwapPosition(SessionControl other)
{
    int myIndex = this.Parent.Controls.GetChildIndex(this);
    int otherIndex = this.Parent.Controls.GetChildIndex(other);

    this.Parent.Controls.SetChildIndex(this, otherIndex);
    this.Parent.Controls.SetChildIndex(other, myIndex);
}
```

- [ ] **Step 4: 在 ClaudeMonitor 主类增加 ReorderSessions 方法**

在 `ClaudeMonitor` 类中增加：

```csharp
public void ReorderSessions()
{
    // 重新计算所有 session 的位置
    int totalHeight = 0;
    for (int i = 0; i < contentPanel.Controls.Count; i++)
    {
        if (contentPanel.Controls[i] is SessionControl ctrl)
        {
            ctrl.Top = totalHeight;
            totalHeight += ctrl.RequiredHeight + 5;
        }
    }

    // 调整窗口高度
    int windowHeight = 24 + totalHeight + 10;
    this.Height = Math.Min(windowHeight, 500);
}
```

- [ ] **Step 5: Commit 拖动排序功能**

```bash
git add Monitor.cs
git commit -m "feat(Monitor): add drag-to-reorder session cards"
```

---

## Task 10: 编译与测试

**Files:**
- All modified files

- [ ] **Step 1: 编译 Monitor.cs**

```bash
cd E:/Code/claude-monitor-release
# 使用 csc 或 Visual Studio 编译 Monitor.cs 为 Monitor.exe
csc /target:winexe /out:Monitor.exe Monitor.cs /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll
```

- [ ] **Step 2: 启动 server.js 测试**

```bash
node server.js
```

- [ ] **Step 3: 启动 Monitor.exe 测试**

```bash
./Monitor.exe
```

- [ ] **Step 4: 触发 hook 测试像素马显示**

在 Claude Code 中执行操作，观察 Monitor 窗口中的像素马是否正确显示。

- [ ] **Step 5: 测试状态切换**

手动发送不同状态测试：
```bash
# 测试 thinking
curl -X POST http://localhost:18989/session -H "Content-Type: application/json" -d '{"state":"thinking","task":"Analyzing...","contextPercentage":50}'

# 测试 working
curl -X POST http://localhost:18989/session -H "Content-Type: application/json" -d '{"state":"working","task":"Running command","contextPercentage":75}'

# 测试 error
curl -X POST http://localhost:18989/session -H "Content-Type: application/json" -d '{"state":"error","task":"Error occurred","contextPercentage":95}'
```

- [ ] **Step 6: Final commit**

```bash
git add -A
git commit -m "feat: complete pixel horse UI optimization for Claude Monitor"
```

---

## Spec Coverage Check

| 需求 | Task |
|------|------|
| 8x8 像素马 | Task 3, 4, 5 |
| Thinking 浅蓝 #5dade2 | Task 3, 8 |
| Working 亮绿 #2ecc71 | Task 3, 8 |
| Error 亮红 #e74c3c | Task 3, 8 |
| Idle 浅灰 #95a5a6 | Task 3, 8 |
| 状态文字加粗放大10% | Task 4 |
| 进度条 10px 高 | Task 5 |
| 进度条颜色阈值 | Task 5 |
| 进度条渐变过渡 | Task 5 |
| 卡片内边距 12px | Task 4 |
| 卡片分割线 | Task 5 |
| 按钮 24x24px | Task 7 |
| 按钮 tooltip | Task 7 |
| 按钮 hover 效果 | Task 7 |
| 点击 scale 0.95 | Task 5 |
| Hover 背景 | Task 5 |
| 拖动排序 | Task 9 |
| server.js contextPercentage | Task 1 |
| hook.js contextPercentage | Task 2 |