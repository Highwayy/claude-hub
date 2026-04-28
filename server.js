const express = require('express');
const fs = require('fs');
const path = require('path');
const { spawn } = require('child_process');

const PORT = 18989;
const DATA_DIR = path.join(process.env.APPDATA || process.env.HOME, 'claude-monitor');
const HISTORY_FILE = path.join(DATA_DIR, 'history.json');
const CONFIG_FILE = path.join(DATA_DIR, 'config.json');
const SESSIONS_FILE = path.join(DATA_DIR, 'sessions.json');

// Ensure data directory exists
if (!fs.existsSync(DATA_DIR)) {
    fs.mkdirSync(DATA_DIR, { recursive: true });
}

// Multi-session support
let sessions = {};
let activeSessionIds = new Set();
let taskHistory = [];
let showWindowFlag = false;  // Flag to signal Monitor to show window
const MAX_HISTORY = 50;

// Monitor 进程协调
let monitorPid = null;
let monitorProcess = null;  // 持有 Monitor 子进程引用
let monitorStarting = false;  // 启动锁，防止重复启动
let monitorStartTimeout = null;
let monitorLastAlive = 0;
let monitorGracefulExit = false;  // 标记Monitor是否优雅退出
const MONITOR_TIMEOUT = 30000; // 30 seconds（给Monitor更多时间）
const MONITOR_START_TIMEOUT = 10000; // 10 seconds
const MONITOR_RESTART_DELAY = 5000; // 退出后等待5秒再重启

// 启动 Monitor 进程（由 server 持有）
function startMonitorProcess() {
    // 如果正在启动，跳过
    if (monitorStarting) {
        console.log('[Monitor] Already starting, skipping...');
        return false;
    }

    const monitorPath = path.join(__dirname, 'Monitor.exe');
    if (!fs.existsSync(monitorPath)) {
        console.error('Monitor.exe not found:', monitorPath);
        return false;
    }

    // 如果已经有进程在运行，不重复启动
    if (monitorProcess && monitorPid) {
        try {
            process.kill(monitorPid, 0);  // 检查进程是否存活
            console.log('Monitor already running, pid:', monitorPid);
            return true;
        } catch (e) {
            // 进程已死，重新启动
            monitorProcess = null;
            monitorPid = null;
        }
    }

    // 设置启动锁
    monitorStarting = true;
    monitorGracefulExit = false;

    try {
        monitorProcess = spawn(monitorPath, [], {
            detached: false,  // 不分离，让 server 作为父进程
            stdio: 'ignore',
            windowsHide: false  // 显示窗口
        });

        // 立即设置 PID，不等待 Monitor 报告
        monitorPid = monitorProcess.pid;
        monitorLastAlive = Date.now();

        monitorProcess.on('exit', (code) => {
            console.log('Monitor exited with code:', code, 'graceful:', monitorGracefulExit);
            monitorProcess = null;
            monitorPid = null;
            monitorLastAlive = 0;  // 清空心跳时间，防止下一个Monitor误判
            // 释放启动锁
            monitorStarting = false;
            if (monitorStartTimeout) {
                clearTimeout(monitorStartTimeout);
                monitorStartTimeout = null;
            }
        });

        monitorProcess.on('error', (err) => {
            console.error('Monitor process error:', err.message);
            monitorStarting = false;
        });

        console.log('Monitor started with pid:', monitorProcess.pid);

        // 设置启动锁超时，防止死锁
        monitorStartTimeout = setTimeout(() => {
            monitorStarting = false;
            monitorStartTimeout = null;
        }, MONITOR_START_TIMEOUT);

        return true;
    } catch (e) {
        console.error('Failed to start Monitor:', e.message);
        monitorStarting = false;
        return false;
    }
}

// Load history from file
function loadHistory() {
    try {
        if (fs.existsSync(HISTORY_FILE)) {
            taskHistory = JSON.parse(fs.readFileSync(HISTORY_FILE, 'utf8'));
        }
    } catch (e) {
        taskHistory = [];
    }
}

// Save history to file
function saveHistory() {
    try {
        fs.writeFileSync(HISTORY_FILE, JSON.stringify(taskHistory.slice(-MAX_HISTORY), null, 2));
    } catch (e) { }
}

// Load sessions from file（启动时恢复）
function loadSessions() {
    try {
        if (fs.existsSync(SESSIONS_FILE)) {
            const data = JSON.parse(fs.readFileSync(SESSIONS_FILE, 'utf8'));
            sessions = data.sessions || {};
            activeSessionIds = new Set(data.activeSessionIds || []);
            console.log('Loaded', activeSessionIds.size, 'sessions from file');
        }
    } catch (e) {
        console.error('Failed to load sessions:', e.message);
        sessions = {};
        activeSessionIds = new Set();
    }
}

// Save sessions to file（debounced，避免频繁写入）
let saveSessionsTimer = null;
function saveSessions() {
    // Debounce: 延迟500ms保存，如果期间有新更新则重置计时器
    if (saveSessionsTimer) clearTimeout(saveSessionsTimer);
    saveSessionsTimer = setTimeout(() => {
        saveSessionsTimer = null;
        try {
            const data = {
                sessions: sessions,
                activeSessionIds: Array.from(activeSessionIds),
                savedAt: Date.now()
            };
            fs.writeFileSync(SESSIONS_FILE, JSON.stringify(data, null, 2));
        } catch (e) { }
    }, 500);
}

// 立即保存（用于退出时）
function saveSessionsImmediate() {
    if (saveSessionsTimer) {
        clearTimeout(saveSessionsTimer);
        saveSessionsTimer = null;
    }
    try {
        const data = {
            sessions: sessions,
            activeSessionIds: Array.from(activeSessionIds),
            savedAt: Date.now()
        };
        fs.writeFileSync(SESSIONS_FILE, JSON.stringify(data, null, 2));
    } catch (e) { }
}

// Add to history
function addToHistory(session) {
    if (session.state === 'complete' && session.task) {
        const entry = {
            time: new Date().toISOString(),
            project: session.project,
            task: session.task,
            id: session.id
        };
        taskHistory.push(entry);
        // Keep only last MAX_HISTORY entries
        if (taskHistory.length > MAX_HISTORY) {
            taskHistory = taskHistory.slice(-MAX_HISTORY);
        }
        saveHistory();
    }
}

// Load history on startup
loadHistory();
loadSessions();

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
            model: null,
            context: null,
            branch: null,
            windowHandle: null,
            userMessage: null,
            needsHandleRecapture: false  // Flag for window rebind
        };
    }
    return sessions[sessionId];
}

// Clean old sessions (older than 60 minutes of inactivity)
setInterval(() => {
    const now = Date.now();
    const INACTIVITY_TIMEOUT = 120 * 60 * 1000; // 120 minutes (2 hours)
    for (const id in sessions) {
        const inactiveMs = now - sessions[id].lastUpdate;
        if (inactiveMs > INACTIVITY_TIMEOUT) {
            const inactiveMinutes = Math.round(inactiveMs / 60000);
            console.log(`[Cleanup] Removing session ${id}: inactive=${inactiveMinutes}min, state=${sessions[id].state}, project=${sessions[id].project}`);
            delete sessions[id];
            activeSessionIds.delete(id);
        }
    }
}, 60000);

// 定期检查 Monitor 进程是否存活，自动重启
let monitorRestartTimer = null;  // 延迟重启定时器
setInterval(() => {
    // 如果正在启动或优雅退出，跳过
    if (monitorStarting || monitorGracefulExit) {
        return;
    }

    const deadTime = Date.now() - monitorLastAlive;
    if (!monitorPid || deadTime > MONITOR_TIMEOUT) {
        // 如果已有延迟重启定时器，跳过
        if (monitorRestartTimer) {
            return;
        }
        console.log('[Monitor Check] Monitor not running (deadTime=' + Math.round(deadTime/1000) + 's), scheduling restart...');
        monitorRestartTimer = setTimeout(() => {
            monitorRestartTimer = null;
            if (!monitorStarting && !monitorGracefulExit) {
                startMonitorProcess();
            }
        }, MONITOR_RESTART_DELAY);
    }
}, 5000); // 每5秒检查一次

const app = express();
app.use(express.json());

// Ensure UTF-8 response
app.use((req, res, next) => {
    res.setHeader('Content-Type', 'application/json; charset=utf-8');
    next();
});

// Get all sessions status
app.get('/status', (req, res) => {
    const sessionList = Object.values(sessions).filter(s => activeSessionIds.has(s.id));

    // Include showWindow flag in response
    const response = {
        showWindow: showWindowFlag
    };

    if (sessionList.length === 0) {
        response.state = 'idle';
        response.task = '';
        response.progress = 0;
        response.message = 'Ready';
        response.sessions = [];
    } else if (sessionList.length === 1) {
        const s = sessionList[0];
        response.state = s.state;
        response.task = s.task;
        response.progress = s.progress;
        response.message = s.message;
        response.sessions = sessionList;
    } else {
        const active = sessionList.filter(s => s.state !== 'idle');
        if (active.length === 1) {
            const s = active[0];
            response.state = s.state;
            response.task = `[${s.project}] ${s.task}`;
            response.progress = s.progress;
            response.message = s.message;
            response.sessions = sessionList;
        } else if (active.length > 1) {
            response.state = 'working';
            response.task = `${active.length} active sessions`;
            response.progress = 50;
            response.message = '';
            response.sessions = sessionList;
        } else {
            response.state = 'idle';
            response.task = `${sessionList.length} sessions idle`;
            response.progress = 0;
            response.message = '';
            response.sessions = sessionList;
        }
    }

    // Clear flag after sending
    if (showWindowFlag) {
        showWindowFlag = false;
    }

    res.json(response);
});

// Update single session (from hooks)
app.post('/session', (req, res) => {
    const { sessionId, project, state, task, progress, message, windowHandle, model, context, branch, userMessage } = req.body;
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
    if (project !== undefined && project !== null) session.project = project;  // 允许动态更新项目名
    // 只在新值非 null 时才更新
    if (model !== undefined && model !== null) session.model = model;
    if (context !== undefined && context !== null) session.context = context;
    if (branch !== undefined && branch !== null) session.branch = branch;
    // userMessage 只在有新值时才更新，保持原值不被覆盖
    if (userMessage !== undefined && userMessage !== null) session.userMessage = userMessage;
    session.lastUpdate = Date.now();

    // 每次更新后保存到文件
    saveSessions();

    // Add to history when task completes
    if (state === 'complete' && prevState !== 'complete') {
        addToHistory(session);
    }

    res.json({ success: true });
});

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

// Mark session for window handle recapture
app.post('/session/:id/recapture-handle', (req, res) => {
    const sid = req.params.id;
    if (sessions[sid]) {
        sessions[sid].needsHandleRecapture = true;
        sessions[sid].lastUpdate = Date.now();
        res.json({ success: true, needsHandleRecapture: true });
    } else {
        res.json({ success: false, error: 'Session not found' });
    }
});

// Check if session needs recapture (for hook)
app.get('/session/:id/needs-recapture', (req, res) => {
    const sid = req.params.id;
    if (sessions[sid]) {
        res.json({ needsHandleRecapture: sessions[sid].needsHandleRecapture || false });
    } else {
        res.json({ needsHandleRecapture: false });
    }
});

// Clear recapture flag after successful capture
app.post('/session/:id/clear-recapture', (req, res) => {
    const sid = req.params.id;
    if (sessions[sid]) {
        sessions[sid].needsHandleRecapture = false;
        sessions[sid].lastUpdate = Date.now();
        res.json({ success: true });
    } else {
        res.json({ success: false });
    }
});

// Update active sessions list
app.post('/active', (req, res) => {
    const { sessions: ids } = req.body;
    if (Array.isArray(ids)) {
        activeSessionIds = new Set(ids);
    }
    res.json({ success: true });
});

// Legacy status update
app.post('/status', (req, res) => {
    const { state, task, progress, message, result, windowHandle } = req.body;
    const sid = 'default';
    const session = getOrCreateSession(sid, 'main');

    // Auto-add to active sessions
    activeSessionIds.add(sid);

    const prevState = session.state;
    if (state) session.state = state;
    if (task !== undefined) session.task = task;
    if (progress !== undefined) session.progress = Math.min(100, Math.max(0, progress));
    if (message !== undefined) session.message = message;
    if (windowHandle !== undefined) session.windowHandle = windowHandle;
    session.lastUpdate = Date.now();

    // Add to history when task completes
    if (state === 'complete' && prevState !== 'complete') {
        addToHistory(session);
    }

    res.json({ success: true });
});

// Get task history
app.get('/history', (req, res) => {
    const limit = parseInt(req.query.limit) || 20;
    res.json(taskHistory.slice(-limit));
});

// Clear history
app.delete('/history', (req, res) => {
    taskHistory = [];
    saveHistory();
    res.json({ success: true });
});

// Get/Set window position
app.get('/config', (req, res) => {
    try {
        if (fs.existsSync(CONFIG_FILE)) {
            res.json(JSON.parse(fs.readFileSync(CONFIG_FILE, 'utf8')));
        } else {
            res.json({});
        }
    } catch (e) {
        res.json({});
    }
});

app.post('/config', (req, res) => {
    try {
        let config = {};
        if (fs.existsSync(CONFIG_FILE)) {
            config = JSON.parse(fs.readFileSync(CONFIG_FILE, 'utf8'));
        }
        Object.assign(config, req.body);
        fs.writeFileSync(CONFIG_FILE, JSON.stringify(config, null, 2));
        res.json({ success: true });
    } catch (e) {
        res.status(500).json({ error: e.message });
    }
});

// Reset
app.post('/reset', (req, res) => {
    sessions = {};
    activeSessionIds = new Set();
    res.json({ success: true });
});

// Health check
app.get('/health', (req, res) => {
    res.json({ status: 'ok', sessions: activeSessionIds.size, uptime: process.uptime() });
});

// Debug endpoint
app.get('/debug', (req, res) => {
    res.json({
        activeSessionIds: Array.from(activeSessionIds),
        sessions: sessions
    });
});

// Get session window handle
app.get('/session/:id/window', (req, res) => {
    const session = sessions[req.params.id];
    if (session && session.windowHandle) {
        res.json({ windowHandle: session.windowHandle });
    } else {
        res.json({ windowHandle: null });
    }
});

// Activate window by handle (called from Monitor)
app.post('/activate-window', (req, res) => {
    const { handle } = req.body;
    // This just returns the handle, actual activation is done by Monitor.cs via Win32
    res.json({ handle: handle });
});

// Show monitor window (called when new instance tries to start)
app.post('/show-window', (req, res) => {
    showWindowFlag = true;
    res.json({ success: true });
});

// Monitor heartbeat (called by Monitor.exe periodically)
app.post('/monitor-heartbeat', (req, res) => {
    const { pid } = req.body;
    monitorLastAlive = Date.now();
    if (pid) monitorPid = pid;
    res.json({ success: true });
});

// 获取 Monitor 进程状态
app.get('/monitor-status', (req, res) => {
    const running = monitorPid && (Date.now() - monitorLastAlive < MONITOR_TIMEOUT);
    res.json({
        running: running,
        pid: monitorPid,
        starting: monitorStarting,
        lastAlive: monitorLastAlive
    });
});

// 设置 Monitor 启动锁
app.post('/monitor-starting', (req, res) => {
    monitorStarting = true;
    if (monitorStartTimeout) clearTimeout(monitorStartTimeout);
    monitorStartTimeout = setTimeout(() => {
        monitorStarting = false;
    }, MONITOR_START_TIMEOUT);
    res.json({ success: true });
});

// Monitor 启动完成，报告 PID
app.post('/monitor-started', (req, res) => {
    const { pid } = req.body;
    if (pid) monitorPid = pid;
    monitorLastAlive = Date.now();  // 立即更新心跳时间
    monitorStarting = false;
    monitorGracefulExit = false;
    if (monitorStartTimeout) {
        clearTimeout(monitorStartTimeout);
        monitorStartTimeout = null;
    }
    console.log('Monitor registered with pid:', pid);
    res.json({ success: true });
});

// Monitor 优雅退出通知（退出前调用）
app.post('/monitor-stopping', (req, res) => {
    const { pid } = req.body;
    console.log('Monitor stopping notification, pid:', pid);
    monitorGracefulExit = true;
    // 取消任何延迟重启定时器
    if (monitorRestartTimer) {
        clearTimeout(monitorRestartTimer);
        monitorRestartTimer = null;
    }
    res.json({ success: true });
});

// 让 server 启动 Monitor（保持进程稳定）
app.post('/start-monitor', (req, res) => {
    const success = startMonitorProcess();
    res.json({ success, pid: monitorProcess ? monitorProcess.pid : null });
});

// Shutdown server - REMOVED to prevent accidental shutdown
// If you need to shutdown, use SIGINT/SIGTERM or manual process kill

const server = app.listen(PORT, () => {
    console.log(`Monitor server: http://localhost:${PORT}`);
    // 启动时自动启动 Monitor.exe
    startMonitorProcess();
});

// Keep-alive settings to prevent connection drops
server.keepAliveTimeout = 65000;
server.headersTimeout = 66000;

// Handle server errors
server.on('error', (err) => {
    if (err.code === 'EADDRINUSE') {
        console.error('Port already in use, exiting');
        process.exit(1);
    }
    console.error('Server error:', err.message);
});

// Global error handlers
process.on('uncaughtException', (err) => {
    console.error('Uncaught exception:', err.message);
    // Don't exit, keep server running
});

process.on('unhandledRejection', (reason) => {
    console.error('Unhandled rejection:', reason);
});

process.on('SIGINT', () => {
    saveHistory();
    saveSessionsImmediate();
    server.close();
    process.exit();
});

process.on('SIGTERM', () => {
    saveHistory();
    saveSessionsImmediate();
    server.close();
    process.exit();
});