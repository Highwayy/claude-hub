# Claude Hub

Claude Hub 是一个 Windows 桌面悬浮监控窗口，用于显示 Claude Code 和 Codex 的多会话状态。它由 Node.js 本地服务负责接收会话事件，由 C# WinForms 窗口负责展示和窗口切换。

## 功能

- 同时显示多个 Claude Code / Codex 会话
- 显示项目名、分支、状态、模型、推理强度和上下文使用率
- Claude Code 会话支持点击切回对应终端窗口
- Codex 会话通过本地 JSONL 会话文件变化主动推送
- 会话删除、重置、窗口位置和历史记录会持久化
- 支持自定义服务端口，默认 `18989`

## 项目结构

```text
claude-monitor/
├── server.js                    # Node.js 状态服务
├── hook.js                      # Claude Code hooks 入口
├── codex-watcher.js             # Codex 会话 JSONL watcher
├── Monitor.cs                   # C# WinForms 悬浮窗口源码
├── Monitor.exe                  # 编译后的桌面窗口程序
├── GetForeground.cs             # 获取前台窗口信息的 C# 源码
├── GetForeground.dll            # PowerShell 调用的窗口信息 DLL
├── get-foreground-window.ps1    # 获取当前前台窗口句柄和类名
├── find-window-by-pid.ps1       # 根据进程树查找终端窗口句柄
├── *.test.js                    # Node.js 回归测试
├── package.json                 # Node.js 脚本和依赖
└── LESSONS.md                   # 项目问题记录
```

## 启动

```powershell
npm install
npm start
```

`server.js` 启动后会自动启动：

- `Monitor.exe`
- `codex-watcher.js`

默认服务地址：

```text
http://127.0.0.1:18989
```

如需使用其他端口：

```powershell
$env:CLAUDE_MONITOR_PORT = "19000"
npm start
```

## Claude Code 集成

在 Claude Code 的 `settings.json` 中配置 hooks，将路径替换成你的实际项目路径：

```json
{
  "hooks": {
    "PreToolUse": [{
      "matcher": ".*",
      "hooks": [{ "type": "command", "command": "node D:/Projects/claude-monitor/hook.js" }]
    }],
    "PostToolUse": [{
      "matcher": ".*",
      "hooks": [{ "type": "command", "command": "node D:/Projects/claude-monitor/hook.js" }]
    }],
    "SessionStart": [{
      "hooks": [{ "type": "command", "command": "node D:/Projects/claude-monitor/hook.js" }]
    }],
    "Stop": [{
      "hooks": [{ "type": "command", "command": "node D:/Projects/claude-monitor/hook.js" }]
    }],
    "UserPromptSubmit": [{
      "hooks": [{ "type": "command", "command": "node D:/Projects/claude-monitor/hook.js" }]
    }],
    "Notification": [{
      "hooks": [{ "type": "command", "command": "node D:/Projects/claude-monitor/hook.js" }]
    }]
  }
}
```

Claude Code hook 会把会话状态、用户输入、工具调用、模型、上下文、分支和窗口句柄发送到本地服务。

## Codex 集成

`codex-watcher.js` 会监听本机 Codex 会话目录：

```text
%USERPROFILE%\.codex\sessions
```

它会增量读取最新 `.jsonl` 文件，并转换为统一的 `/session` payload。支持的内容包括：

- 用户消息
- 助手消息
- reasoning / function call / command activity
- `complete` 状态
- 模型和推理强度
- 上下文百分比
- 项目名和 Git 分支

如果 Codex sessions 目录在启动时不存在，watcher 会保持运行并定期重试，目录出现后自动开始监听。

## API

服务默认监听 `http://127.0.0.1:18989`。

| Endpoint | Method | Description |
| --- | --- | --- |
| `/health` | GET | 健康检查 |
| `/status` | GET | 获取当前会话列表和窗口显示状态 |
| `/session` | POST | 更新单个会话状态 |
| `/session/:id` | DELETE | 删除单个会话 |
| `/session/:id/recapture-handle` | POST | 标记 Claude 会话需要重新绑定窗口 |
| `/session/:id/needs-recapture` | GET | 查询是否需要重新绑定窗口 |
| `/session/:id/clear-recapture` | POST | 清除重新绑定标记 |
| `/history` | GET | 获取任务历史 |
| `/history` | DELETE | 清空任务历史 |
| `/reset` | POST | 清空所有当前会话 |
| `/start-monitor` | POST | 启动 Monitor 窗口 |
| `/monitor-heartbeat` | POST | Monitor 心跳 |
| `/monitor-status` | GET | Monitor 进程状态 |

## 会话状态

| State | Meaning |
| --- | --- |
| `idle` | 空闲 |
| `thinking` | 思考中 |
| `working` | 执行中 |
| `waiting` | 等待用户输入或权限确认 |
| `complete` | 完成 |
| `error` | 出错 |

## 数据存储

运行数据保存在：

```text
%APPDATA%\claude-monitor\
```

主要文件：

- `config.json`：窗口位置
- `sessions.json`：当前会话和 active 会话列表
- `history.json`：任务历史
- `window-handles.json`：Claude 会话到终端窗口句柄的映射
- `hook-debug.log`：Claude hook 日志
- `codex-watcher.log`：Codex watcher 日志
- `monitor.log`：Monitor 窗口日志

## 测试

```powershell
npm test
```

当前测试覆盖：

- Codex JSONL 事件解析
- Codex watcher 启动预读、目录重试、文件串行处理
- 自定义端口配置
- hook 健康检查
- server 删除和 reset 持久化
- Monitor JSON 字符串转义解析

## 编译 Monitor

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" `
  /nologo `
  /target:winexe `
  /platform:x64 `
  /out:Monitor.exe `
  /reference:System.dll `
  /reference:System.Drawing.dll `
  /reference:System.Windows.Forms.dll `
  Monitor.cs
```

## 测试环境变量

| Variable | Description |
| --- | --- |
| `CLAUDE_MONITOR_PORT` | 指定本地服务端口，默认 `18989` |
| `CLAUDE_MONITOR_DISABLE_AUTOSTART=1` | 禁止 `server.js` 自动启动 Monitor 和 Codex watcher，主要用于测试 |
