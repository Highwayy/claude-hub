# Claude Hub

Claude Code 多会话状态监控窗口，支持多终端标签页窗口切换。

## 项目结构

```
claude-monitor/
├── server.js              # Node.js 状态服务器 (端口 18989)
├── hook.js                # Claude Code hooks 脚本
├── Monitor.cs             # C# 悬浮窗口源码
├── Monitor.exe            # 编译后的窗口程序 (Claude Hub)
├── GetForeground.cs       # 获取前台窗口的 C# 源码
├── GetForeground.dll      # 编译后的 DLL
├── get-foreground-window.ps1  # 调用 DLL 的 PowerShell 脚本
├── find-window-by-pid.ps1     # 通过进程树查找窗口（备用）
├── package.json           # 依赖配置
├── LESSONS.md             # 项目踩坑记录
└── README.md              # 本文件
```

## 启动方式

```bash
# 启动服务器（Monitor.exe 会自动启动）
node server.js
```

## Claude Code 集成

在 Claude Code 的 `settings.json` 配置 hooks:

```json
{
  "hooks": {
    "PreToolUse": [{
      "matcher": ".*",
      "hooks": [{ "type": "command", "command": "node E:/Code/claude-monitor/hook.js" }]
    }],
    "PostToolUse": [{
      "matcher": ".*",
      "hooks": [{ "type": "command", "command": "node E:/Code/claude-monitor/hook.js" }]
    }],
    "SessionStart": [{
      "hooks": [{ "type": "command", "command": "node E:/Code/claude-monitor/hook.js" }]
    }],
    "Stop": [{
      "hooks": [{ "type": "command", "command": "node E:/Code/claude-monitor/hook.js" }]
    }],
    "UserPromptSubmit": [{
      "hooks": [{ "type": "command", "command": "node E:/Code/claude-monitor/hook.js" }]
    }]
  }
}
```

## 功能特性

- **多会话支持**: 同时监控多个 Claude Code 会话
- **窗口切换**: 点击会话可切换到对应的终端窗口
- **状态显示**: 实时显示任务状态、项目名、分支、上下文使用率
- **任务历史**: 记录完成的任务

## API 端点

服务器: `http://localhost:18989`

| 端点 | 方法 | 说明 |
|------|------|------|
| `/status` | GET | 获取当前状态 |
| `/session` | POST | 更新会话状态 |
| `/session/:id` | DELETE | 删除会话 |
| `/history` | GET | 任务历史 |
| `/start-monitor` | POST | 启动 Monitor 进程 |
| `/health` | GET | 健康检查 |

## 状态值

- `idle` - 空闲
- `thinking` - 思考中
- `working` - 执行中
- `waiting` - 等待输入
- `complete` - 完成
- `error` - 出错

## 数据存储

配置和历史存储在 `%APPDATA%\claude-monitor\`:

- `config.json` - 窗口位置配置
- `history.json` - 任务历史
- `window-handles.json` - 会话窗口句柄映射
- `hook-debug.log` - Hook 调试日志
- `monitor.log` - Monitor 日志