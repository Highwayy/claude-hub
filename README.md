# Claude Code Monitor

Claude Code 状态监控窗口，显示当前任务执行状态。

## 项目结构

```
claude-monitor/
├── server.js      # Node.js 服务器 (端口 18989)
├── Monitor.cs     # C# 悬浮窗口源码
├── Monitor.exe    # 编译后的窗口程序
├── hook.js        # Claude Code hooks 脚本
└── package.json   # 依赖配置 (仅 express)
```

## 启动方式

```bash
# 启动服务器
node server.js

# Monitor.exe 会自动启动（由 server 管理）
```

## API 端点

服务器: `http://localhost:18989`

| 端点 | 说明 |
|------|------|
| `GET /status` | 获取当前状态 |
| `POST /status` | 更新状态 |
| `POST /session` | 更新会话状态 |
| `POST /reset` | 重置状态 |
| `GET /history` | 任务历史 |

## 状态值

- `idle` - 空闲
- `thinking` - 思考中
- `working` - 执行中
- `waiting` - 等待输入
- `complete` - 完成
- `error` - 出错

## Claude Code 集成

在 `settings.local.json` 配置 hooks:

```json
{
  "hooks": {
    "PreToolUse": [{
      "matcher": ".*",
      "hooks": [{ "type": "command", "command": "node E:/Code/claude-monitor/hook.js pre" }]
    }],
    "PostToolUse": [{
      "matcher": ".*",
      "hooks": [{ "type": "command", "command": "node E:/Code/claude-monitor/hook.js post" }]
    }]
  }
}
```