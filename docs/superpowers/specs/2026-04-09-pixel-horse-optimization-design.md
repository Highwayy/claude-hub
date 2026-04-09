# Claude Monitor 像素风格优化设计

## 背景

Claude Monitor 是 Claude Code 的状态监控窗口，当前使用简单的圆点指示器和基础颜色方案。本设计旨在：
- 引入像素风格马图标，增强视觉趣味性
- 强化状态颜色区分，提升用户体验
- 优化上下文进度条可视化
- 改进卡片布局和交互反馈

## 设计决策

### 1. 像素马风格

**选择**：8x8 像素马（Option 2 from brainstorming）

**原因**：
- 经典复古游戏风格，与 Claude Code 极客气质匹配
- 尺寸适中，不会占用过多空间
- 动画帧数量可控（3-4帧即可实现流畅动画）

### 2. 状态配色

**选择**：配色方案 A（用户原始需求）

| 状态 | 颜色 | Hex | RGB |
|------|------|-----|-----|
| Thinking | 浅蓝/青蓝 | #5dade2 | (93, 173, 226) |
| Working | 亮绿 | #2ecc71 | (46, 204, 113) |
| Error | 亮红 | #e74c3c | (231, 76, 60) |
| Idle | 浅灰 | #95a5a6 | (149, 165, 166) |

### 3. 进度条样式

**选择**：渐变过渡（Option B from brainstorming）

**实现**：
- 高度：10px（从 3px 增加）
- 阈值变色：
  - <70%：绿色渐变 `linear-gradient(90deg, #2ecc71, #27ae60)`
  - 70-90%：黄色渐变 `linear-gradient(90deg, #f39c12, #e67e22)`
  - >90%：红色渐变 `linear-gradient(90deg, #e74c3c, #c0392b)`
- 圆角：4px

### 4. 卡片布局

**选择**：分割线 + 内边距（Option B from brainstorming）

**实现**：
- 内边距：12px（从 8px 增加）
- 分割线：`1px solid rgba(255,255,255,0.08)`
- 状态文字：font-size 12px（放大 10%）

### 5. 标题栏按钮

**选择**：图标按钮 + 阴影（Option B from brainstorming）

**实现**：
- 尺寸：24x24px（与标题栏高度 24px 对齐）
- 图标：📌（钉住）、✕（关闭）
- 背景：`rgba(255,255,255,0.2)`
- Hover：`rgba(255,255,255,0.3)` + 轻微阴影
- Tooltip：悬停提示

### 6. 交互反馈

**实现**：
- 点击：`scale(0.95)` + `opacity: 0.8`
- Hover：背景变亮 `#242438 → #2a2a3a`
- 过渡：`transition: all 0.15s ease`

### 7. 可选功能

**选择**：仅拖动卡片排序（第一个选项）

**不实现**：卡片折叠/展开

## 技术设计

### 像素马 Sprite 定义

使用 C# Bitmap 或直接绘制像素块。每个状态有独立的像素定义：

```
// 8x8 像素马基础模板（Working 状态）
// 马头位置固定，腿部动画变化

Frame 1 (静止):
  ▪▪▪▪▪▪▪▪
  ▪▪▪🐴▪▪▪▪
  ▪▪▪▪▪▪▪▪
  ▪▪ ▪▪▪▪▪▪

Frame 2 (奔跑左腿前):
  ▪▪▪▪▪▪▪▪
  ▪▪▪🐴▪▪▪▪
  ▪▪ ▪▪▪▪▪▪
  ▪▪▪ ▪▪▪▪▪

Frame 3 (奔跑右腿前):
  ▪▪▪▪▪▪▪▪
  ▪▪▪🐴▪▪▪▪
  ▪▪▪▪ ▪▪▪▪
  ▪▪ ▪▪▪▪▪▪
```

**Thinking 状态**：添加旋转符号 ↻ 替换眼睛位置
**Error 状态**：添加皱眉符号 😠 或用像素绘制皱眉表情
**Idle 状态**：添加静止符号 — 或保持默认表情

### Monitor.cs 修改清单

#### 1. 像素马绘制类

```csharp
class PixelHorseSprite
{
    private Bitmap[] frames;
    private int currentFrame;
    private Color horseColor;
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
            default: return Color.FromArgb(149, 165, 166);        // #95a5a6
        }
    }

    public Bitmap GetCurrentFrame() { ... }
    public void AdvanceFrame() { ... }
}
```

#### 2. SessionControl 改动

- 移除 dot Label
- 新增 PixelHorseSprite 绘制区域（PictureBox 或直接 Paint）
- 状态文字样式：`Font = new Font("Segoe UI", 12, FontStyle.Bold)`
- 内边距：padding 12px
- 分割线：底部 `border-bottom` 或 Paint 事件绘制
- 进度条：高度 10px，颜色根据百分比动态变化

#### 3. 交互反馈

```csharp
// MouseDown
private void OnMouseDown(...)
{
    this.BackColor = Color.FromArgb(42, 42, 58); // hover 颜色变亮
}

// MouseClick
private void OnMouseClick(...)
{
    // 点击缩放效果通过动画 timer 实现
    clickScale = 0.95f;
    clickTimer.Start();
}

// Paint 事件绘制像素马
protected override void OnPaint(PaintEventArgs e)
{
    // 绘制像素马 sprite
    e.Graphics.DrawImage(sprite.GetCurrentFrame(), 5, 5);

    // 绘制进度条（根据 context 百分比变色）
    DrawProgressBar(e.Graphics);

    // 绘制分割线
    e.Graphics.DrawLine(pen, 0, this.Height - 1, this.Width, this.Height - 1);
}
```

#### 4. 标题栏按钮改动

- 按钮尺寸：24x24px
- 图标化：使用 Unicode 字符或小图标
- Tooltip：ToolTip 控件添加提示
- Hover 效果：MouseEnter/MouseLeave 事件

#### 5. 拖动排序实现

```csharp
// SessionControl 拖动检测
private bool isDragging = false;
private int dragStartY;
private int originalIndex;

private void OnMouseDown(...)
{
    if (e.Y < 20) // 只允许拖动状态行区域
    {
        isDragging = true;
        dragStartY = e.Y;
        originalIndex = Parent.Controls.IndexOf(this);
        this.BackColor = Color.FromArgb(40, 40, 60);
    }
}

private void OnMouseMove(...)
{
    if (isDragging)
    {
        // 计算新位置
        int newY = this.Top + e.Y - dragStartY;
        // 检测与其他卡片的交换位置
        ...
    }
}

private void OnMouseUp(...)
{
    isDragging = false;
    // 重新排列 sessionControls 列表
    // 触发父容器重新布局
}
```

### server.js 修改清单

需要从 hook.js 获取 context 百分比数据：

```javascript
// session 对象扩展
sessions[sessionId] = {
    id, project, state, task, progress, message, windowHandle,
    contextPercentage: 0,  // 新增：上下文使用百分比
    ...
};

// GET /status 返回增加 contextPercentage
```

### hook.js 修改清单

```javascript
// extractTaskFromInput 增加 context 百分比计算
function extractTaskFromInput(input) {
    let contextPercentage = 0;
    if (input.context_tokens && input.context_window_size) {
        contextPercentage = Math.round(
            (input.context_tokens / input.context_window_size) * 100
        );
    }
    return { ..., contextPercentage };
}
```

## 文件变更清单

| 文件 | 变更内容 |
|------|----------|
| Monitor.cs | 像素马Sprite类、SessionControl布局重构、进度条样式、按钮样式、交互反馈、拖动排序 |
| server.js | session 增加 contextPercentage 字段 |
| hook.js | 提取 context 百分比数据 |

## 实现优先级

1. 像素马 Sprite 绘制（核心视觉）
2. 状态颜色 + 文字样式
3. 进度条样式
4. 卡片布局 + 分割线
5. 标题栏按钮样式
6. 交互反馈效果
7. 拖动排序功能

## 视觉一致性

- 所有像素马统一尺寸 8x8
- 统一 1px 深色描边确保清晰度
- 颜色方案与 Claude Code 品牌一致
- 保持简洁、专业、极客风格