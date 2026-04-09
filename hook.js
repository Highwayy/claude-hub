#!/usr/bin/env node
/**
 * Claude Monitor Hook
 *
 * Receives hook data from Claude Code via stdin and sends status updates to monitor server.
 * Auto-starts server and monitor if not running.
 */

const http = require('http');
const fs = require('fs');
const path = require('path');
const { spawn, execSync } = require('child_process');

const PORT = 18989;
const HOOK_DIR = __dirname;
const DEBUG_FILE = path.join(process.env.APPDATA || process.env.HOME, 'claude-monitor', 'hook-debug.log');

// 项目操作统计（sessionId -> {project -> count}）
const projectStats = {};

// 从文件路径提取项目名（取项目根目录或最后两级目录）
function extractProjectFromPath(filePath, cwd) {
    if (!filePath) return null;

    // 标准化路径
    const normalized = filePath.replace(/\\/g, '/');

    // 尝试找到项目根目录（包含 .git 的目录）
    let dir = path.dirname(normalized);
    while (dir && dir !== '.' && dir !== '/') {
        const gitDir = path.join(dir, '.git');
        try {
            if (fs.existsSync(gitDir)) {
                return path.basename(dir);
            }
        } catch (e) {}
        const parent = path.dirname(dir);
        if (parent === dir) break;
        dir = parent;
    }

    // 回退：取最后两级目录或最后一级
    const parts = normalized.split('/').filter(p => p);
    if (parts.length >= 2) {
        // 如果路径很长，取倒数第二级（项目名）+ 最后一级
        return parts[parts.length - 2] || parts[parts.length - 1];
    }
    return parts[parts.length - 1] || null;
}

// 更新项目统计并返回最活跃的项目
function updateProjectStats(sessionId, filePath, cwd) {
    if (!sessionId) return null;

    const projectName = extractProjectFromPath(filePath, cwd);
    if (!projectName) return null;

    if (!projectStats[sessionId]) {
        projectStats[sessionId] = {};
    }
    projectStats[sessionId][projectName] = (projectStats[sessionId][projectName] || 0) + 1;

    // 返回操作最多的项目
    const stats = projectStats[sessionId];
    let maxCount = 0;
    let activeProject = null;
    for (const [proj, count] of Object.entries(stats)) {
        if (count > maxCount) {
            maxCount = count;
            activeProject = proj;
        }
    }
    return activeProject;
}

// 通过 PID 获取窗口句柄
function getWindowHandleByPid(pid) {
    try {
        if (process.platform !== 'win32' || !pid) return null;
        const scriptPath = path.join(HOOK_DIR, 'find-window-by-pid.ps1');
        const result = execSync(
            'powershell -ExecutionPolicy Bypass -File "' + scriptPath + '" -processId ' + pid,
            { encoding: 'utf8', timeout: 10000 }
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

// 获取当前进程的控制台窗口句柄
function getConsoleWindowHandle() {
    try {
        if (process.platform !== 'win32') return null;
        const scriptPath = path.join(HOOK_DIR, 'get-console-window.ps1');
        const result = execSync(
            'powershell -ExecutionPolicy Bypass -File "' + scriptPath + '"',
            { encoding: 'utf8', timeout: 5000 }
        );
        const handle = result.trim();
        if (handle && handle !== '0') {
            log('getConsoleWindowHandle: handle=' + handle);
            return handle;
        }
    } catch (e) {
        log('getConsoleWindowHandle error: ' + e.message);
    }
    return null;
}

function log(msg) {
    try {
        const dir = path.dirname(DEBUG_FILE);
        if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });
        fs.appendFileSync(DEBUG_FILE, `[${new Date().toISOString()}] ${msg}\n`);
    } catch (e) {}
}

function checkServerRunning() {
    return new Promise((resolve) => {
        const req = http.request({
            hostname: '127.0.0.1',
            port: PORT,
            path: '/health',
            method: 'GET',
            family: 4,                    // 强制使用 IPv4
            timeout: 5000,                // 延长超时至 5 秒
            lookup: (hostname, options, callback) => {
                // 强制解析为 127.0.0.1，彻底避免 DNS 影响
                callback(null, '127.0.0.1', 4);
            }
        }, (res) => {
            log('checkServerRunning: got response ' + res.statusCode);
            res.on('data', () => {});  // 消费响应体
            res.on('end', () => resolve(true));
        });
        req.on('error', (e) => {
            log('checkServerRunning error: ' + e.code + ' - ' + e.message);
            resolve(false);
        });
        req.on('timeout', () => {
            log('checkServerRunning timeout');
            req.destroy();
            resolve(false);
        });
        req.end();
    });
}

// 带重试的服务器检测
async function checkServerRunningWithRetry(maxRetries = 3) {
    for (let i = 0; i < maxRetries; i++) {
        const running = await checkServerRunning();
        if (running) return true;
        if (i < maxRetries - 1) {
            await new Promise(r => setTimeout(r, 500));  // 等待500ms后重试
        }
    }
    return false;
}

async function getMonitorStatus() {
    return new Promise((resolve) => {
        const req = http.request({
            hostname: '127.0.0.1',
            port: PORT,
            path: '/monitor-status',
            method: 'GET',
            family: 4,
            timeout: 5000,
            lookup: (hostname, options, callback) => {
                callback(null, '127.0.0.1', 4);
            }
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
        req.on('error', (e) => {
            log('getMonitorStatus error: ' + e.code);
            resolve({ running: false, pid: null, starting: false });
        });
        req.on('timeout', () => { req.destroy(); resolve({ running: false, pid: null, starting: false }); });
        req.end();
    });
}

async function setMonitorStarting() {
    return new Promise((resolve) => {
        const req = http.request({
            hostname: '127.0.0.1',
            port: PORT,
            path: '/monitor-starting',
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            family: 4,
            timeout: 5000,
            lookup: (hostname, options, callback) => {
                callback(null, '127.0.0.1', 4);
            }
        }, (res) => resolve(true));
        req.on('error', (e) => { log('setMonitorStarting error: ' + e.code); resolve(false); });
        req.write('{}');
        req.end();
    });
}

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

async function ensureMonitorRunning() {
    try {
        const serverRunning = await checkServerRunningWithRetry(3);
        if (!serverRunning) {
            log('Server not running after retries in ensureMonitorRunning, starting...');
            await startServerAndMonitor();
            return;
        }

        // 获取 Monitor 状态
        const status = await getMonitorStatus();
        log('Monitor status: ' + JSON.stringify(status));

        // 如果正在启动中，等待
        if (status.starting) {
            log('Monitor starting by another session, waiting...');
            await new Promise(r => setTimeout(r, 2000));
            return;
        }

        // 如果 PID 存在且进程存活，不启动新的
        if (status.pid) {
            const processExists = await checkProcessExists(status.pid);
            log('Process ' + status.pid + ' exists: ' + processExists);
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

        // 等待 Monitor 向 server 报告 PID（最多等待3秒）
        log('Waiting for Monitor to register...');
        for (let i = 0; i < 6; i++) {
            await new Promise(r => setTimeout(r, 500));
            const newStatus = await getMonitorStatus();
            if (newStatus.pid && newStatus.running) {
                log('Monitor registered with pid=' + newStatus.pid);
                return;
            }
        }
        log('Monitor registration timeout, but continuing...');
    } catch (e) {
        log('ensureMonitorRunning error: ' + e.message);
    }
}

function startMonitor() {
    // 通过 server 启动 Monitor，让 server 作为父进程保持它稳定运行
    const req = http.request({
        hostname: '127.0.0.1',
        port: PORT,
        path: '/start-monitor',
        method: 'POST',
        family: 4,
        timeout: 5000,
        lookup: (hostname, options, callback) => {
            callback(null, '127.0.0.1', 4);
        }
    }, (res) => {
        let data = '';
        res.on('data', chunk => data += chunk);
        res.on('end', () => {
            try {
                const result = JSON.parse(data);
                log('Monitor started via server, pid: ' + result.pid);
            } catch (e) {
                log('start-monitor response parse error');
            }
        });
    });
    req.on('error', (e) => {
        log('start-monitor error: ' + e.code);
        // 如果 server 调用失败，回退到直接启动
        fallbackStartMonitor();
    });
    req.write('{}');
    req.end();
}

// 回退方案：直接启动（可能不稳定）
function fallbackStartMonitor() {
    const monitorPath = path.join(HOOK_DIR, 'Monitor.exe');
    if (fs.existsSync(monitorPath)) {
        const child = spawn(monitorPath, [], {
            detached: false,  // 不分离
            stdio: 'ignore',
            windowsHide: true
        });
        log('Monitor started directly with pid: ' + child.pid);
    }
}

async function getGitBranch(dir) {
    if (!dir) return null;
    try {
        // 如果是文件路径，取其目录
        const stat = fs.statSync(dir);
        if (stat.isFile()) {
            dir = path.dirname(dir);
        }
    } catch (e) {}

    try {
        const result = execSync('git branch --show-current', {
            cwd: dir,
            encoding: 'utf8',
            timeout: 2000
        });
        return result.trim() || null;
    } catch (e) {
        return null;
    }
}

// Get token metrics from transcript file (like ccstatusline)
function getTokenMetricsFromTranscript(transcriptPath) {
    try {
        if (!transcriptPath || !fs.existsSync(transcriptPath)) {
            return null;
        }

        const content = fs.readFileSync(transcriptPath, 'utf8');
        const lines = content.trim().split('\n');

        let contextLength = 0;
        let mostRecentMainChainEntry = null;
        let mostRecentTimestamp = null;

        for (const line of lines) {
            try {
                const data = JSON.parse(line);
                if (data?.message?.usage) {
                    if (data.isSidechain !== true && data.timestamp && !data.isApiErrorMessage) {
                        const entryTime = new Date(data.timestamp);
                        if (!mostRecentTimestamp || entryTime > mostRecentTimestamp) {
                            mostRecentTimestamp = entryTime;
                            mostRecentMainChainEntry = data;
                        }
                    }
                }
            } catch (e) {}
        }

        if (mostRecentMainChainEntry?.message?.usage) {
            const usage = mostRecentMainChainEntry.message.usage;
            contextLength = (usage.input_tokens || 0) +
                           (usage.cache_read_input_tokens || 0) +
                           (usage.cache_creation_input_tokens || 0);
        }

        return { contextLength };
    } catch (e) {
        return null;
    }
}

// Default context window sizes for different models (like ccstatusline)
const DEFAULT_CONTEXT_WINDOW_SIZE = 200000;
const USABLE_CONTEXT_RATIO = 0.8;

function getContextWindowSize(model) {
    // Known model context sizes
    const modelContextSizes = {
        'claude-opus': 200000,
        'claude-sonnet': 200000,
        'claude-haiku': 200000,
        'glm-4': 128000,
        'glm-5': 128000
    };

    if (model) {
        for (const [prefix, size] of Object.entries(modelContextSizes)) {
            if (model.includes(prefix)) {
                return size;
            }
        }
    }
    return DEFAULT_CONTEXT_WINDOW_SIZE;
}

async function startServerAndMonitor() {
    log('Starting server and monitor...');

    // Start server.js in background
    const serverPath = path.join(HOOK_DIR, 'server.js');
    if (fs.existsSync(serverPath)) {
        const serverChild = spawn('node', [serverPath], {
            detached: true,
            stdio: 'ignore',
            windowsHide: true
        });
        serverChild.unref();  // 让子进程完全独立
        log('Server started with pid: ' + serverChild.pid);
    }

    // Wait for server to be ready
    await new Promise(r => setTimeout(r, 1000));

    startMonitor();
}

function sendStatus(data) {
    log('sendStatus: ' + JSON.stringify(data).substring(0, 200));
    return new Promise((resolve) => {
        const body = JSON.stringify(data);
        const req = http.request({
            hostname: '127.0.0.1',
            port: PORT,
            path: '/session',
            method: 'POST',
            headers: {
                'Content-Type': 'application/json; charset=utf-8',
                'Content-Length': Buffer.byteLength(body)
            },
            family: 4,
            timeout: 5000,
            lookup: (hostname, options, callback) => {
                callback(null, '127.0.0.1', 4);
            }
        }, (res) => resolve(true));
        req.on('error', (e) => { log('sendStatus error: ' + e.code + ' - ' + e.message); resolve(false); });
        req.on('timeout', () => { req.destroy(); resolve(false); });
        req.write(body);
        req.end();
    });
}

function truncate(str, maxLen) {
    if (!str) return '';
    if (str.length <= maxLen) return str;
    return str.substring(0, maxLen - 3) + '...';
}

function extractLastAssistantMessage(transcriptPath) {
    try {
        if (!transcriptPath || !fs.existsSync(transcriptPath)) {
            log('transcript not found: ' + transcriptPath);
            return null;
        }

        const content = fs.readFileSync(transcriptPath, 'utf8');
        log('transcript length: ' + content.length);

        const lines = content.trim().split('\n');
        let lastAssistantText = '';

        for (let i = lines.length - 1; i >= 0; i--) {
            try {
                const entry = JSON.parse(lines[i]);

                if (entry.role === 'assistant' && entry.content) {
                    if (typeof entry.content === 'string') {
                        lastAssistantText = entry.content;
                        break;
                    } else if (Array.isArray(entry.content)) {
                        const textBlocks = entry.content
                            .filter(b => b.type === 'text')
                            .map(b => b.text)
                            .join('\n');
                        if (textBlocks) {
                            lastAssistantText = textBlocks;
                            break;
                        }
                    }
                }

                if (entry.message && entry.message.role === 'assistant') {
                    const msg = entry.message;
                    if (typeof msg.content === 'string') {
                        lastAssistantText = msg.content;
                        break;
                    } else if (Array.isArray(msg.content)) {
                        const textBlocks = msg.content
                            .filter(b => b.type === 'text')
                            .map(b => b.text)
                            .join('\n');
                        if (textBlocks) {
                            lastAssistantText = textBlocks;
                            break;
                        }
                    }
                }
            } catch (e) {}
        }

        if (lastAssistantText) {
            log('found assistant text: ' + lastAssistantText.substring(0, 100));
            return lastAssistantText;
        }

        return null;
    } catch (e) {
        log('extractLastAssistantMessage error: ' + e.message);
        return null;
    }
}

function extractTaskFromInput(input) {
    const toolName = input.tool_name || '';
    const toolInput = input.tool_input || {};
    const hookEvent = input.hook_event_name || '';

    log('hook event: ' + hookEvent + ', tool: ' + toolName);

    // 从 hook input 中提取 model 和 context
    let model = null;

    // Handle model as string or object (like ccstatusline)
    if (input.model) {
        if (typeof input.model === 'string') {
            model = input.model;
        } else if (input.model.id || input.model.display_name) {
            model = input.model.id || input.model.display_name;
        }
    }

    let context = null;

    // 计算 context 显示值 (从 context_window 字段，如 ccstatusline)
    const contextWindow = input.context_window;
    if (contextWindow) {
        if (contextWindow.used_percentage !== null && contextWindow.used_percentage !== undefined) {
            context = Math.round(contextWindow.used_percentage) + '%';
        } else if (contextWindow.current_usage) {
            // current_usage can be number or object
            if (typeof contextWindow.current_usage === 'number') {
                context = Math.round(contextWindow.current_usage / 1000) + 'k';
            } else if (contextWindow.current_usage.input_tokens) {
                const inputTokens = contextWindow.current_usage.input_tokens || 0;
                const cacheTokens = (contextWindow.current_usage.cache_creation_input_tokens || 0) +
                                   (contextWindow.current_usage.cache_read_input_tokens || 0);
                const total = inputTokens + cacheTokens;
                context = Math.round(total / 1000) + 'k';
            }
        }
    }

    // If context not from hook input, try transcript file
    if (!context && input.transcript_path) {
        const tokenMetrics = getTokenMetricsFromTranscript(input.transcript_path);
        if (tokenMetrics && tokenMetrics.contextLength > 0) {
            const windowSize = getContextWindowSize(model);
            const usableTokens = Math.floor(windowSize * USABLE_CONTEXT_RATIO);
            const percentage = Math.min(100, Math.round(tokenMetrics.contextLength / usableTokens * 100));
            context = percentage + '%';
        }
    }

    if (hookEvent === 'UserPromptSubmit' && input.prompt) {
        return {
            state: 'working',
            task: 'Processing request...',
            message: 'Processing user request...',
            model: model,
            context: context,
            branch: null,
            userMessage: truncate(input.prompt, 200)
        };
    }

    if (hookEvent === 'Stop') {
        log('Stop event, reason: ' + input.reason + ', transcript_path: ' + input.transcript_path);

        let taskText = input.reason || 'Task completed';

        const lastMsg = extractLastAssistantMessage(input.transcript_path);
        if (lastMsg) {
            taskText = lastMsg;
        }

        taskText = truncate(taskText, 200);

        return {
            state: 'complete',
            task: taskText,
            message: 'Ready',
            model: model,
            context: context,
            branch: null
        };
    }

    if (hookEvent === 'SessionStart') {
        return {
            state: 'idle',
            task: 'Session started',
            message: 'Ready',
            model: model,
            context: context,
            branch: null
        };
    }

    let task = '';
    let state = hookEvent === 'PreToolUse' ? 'working' : 'thinking';

    switch (toolName) {
        case 'Bash':
            task = toolInput.command || 'Running command...';
            if (task.length > 80) {
                task = task.substring(0, 77) + '...';
            }
            break;

        case 'Edit':
        case 'MultiEdit':
            task = 'Editing: ' + (toolInput.file_path || 'file');
            break;

        case 'Write':
            task = 'Writing: ' + (toolInput.file_path || 'file');
            break;

        case 'Read':
            task = 'Reading: ' + (toolInput.file_path || 'file');
            break;

        case 'Glob':
            task = 'Searching: ' + (toolInput.pattern || 'files');
            break;

        case 'Grep':
            task = 'Grep: ' + (toolInput.pattern || 'pattern');
            break;

        case 'WebFetch':
            task = 'Fetching: ' + (toolInput.url || 'URL');
            break;

        case 'WebSearch':
            task = 'Searching: ' + (toolInput.query || 'web');
            break;

        case 'Agent':
            task = 'Running agent: ' + (toolInput.subagent_type || 'task');
            break;

        case 'TaskCreate':
        case 'TaskUpdate':
        case 'TaskList':
        case 'TaskGet':
            task = 'Managing tasks...';
            break;

        case 'AskUserQuestion':
            task = 'Waiting for user input...';
            state = 'waiting';
            break;

        default:
            if (toolName) {
                task = 'Using: ' + toolName;
            } else {
                task = 'Processing...';
            }
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

async function main() {
    const args = process.argv.slice(2);

    // Check if server is running with retry, if not start it
    const serverRunning = await checkServerRunningWithRetry(3);
    if (!serverRunning) {
        log('Server not running after 3 retries, starting...');
        await startServerAndMonitor();
        // Wait a bit for server to be ready
        await new Promise(r => setTimeout(r, 2000));
    } else {
        // Server is running, check if Monitor is still alive
        const monitorStatus = await getMonitorStatus();
        if (!monitorStatus.running || !(await checkProcessExists(monitorStatus.pid))) {
            log('Monitor not running, starting...');
            await ensureMonitorRunning();
        }
    }

    // Read from stdin for proper hook mode
    let inputData = '';

    // Quick timeout for cases where stdin is empty
    const timeout = setTimeout(async () => {
        await sendStatus({
            state: 'working',
            task: 'Processing...',
            message: 'Processing...'
        });
        process.exit(0);
    }, 100);

    process.stdin.setEncoding('utf8');

    process.stdin.on('data', (chunk) => {
        clearTimeout(timeout);
        inputData += chunk;
    });

    process.stdin.on('end', async () => {
        try {
            log('received input: ' + inputData.substring(0, 500));
            const input = JSON.parse(inputData);
            const status = extractTaskFromInput(input);
            const hookEvent = input.hook_event_name || '';

            // SessionStart 时确保 Monitor 运行
            if (hookEvent === 'SessionStart') {
                log('SessionStart event, ensuring monitor...');
                await ensureMonitorRunning();
            }

            if (input.session_id) {
                status.sessionId = input.session_id;
            }
            if (input.cwd) {
                const parts = input.cwd.replace(/\\/g, '/').split('/');
                status.project = parts[parts.length - 1] || input.cwd;
            }

            // 根据工具调用中的文件路径更新项目名
            const toolInput = input.tool_input || {};
            const filePath = toolInput.file_path || toolInput.path || toolInput.dest;
            if (filePath && input.session_id) {
                const activeProject = updateProjectStats(input.session_id, filePath, input.cwd);
                if (activeProject) {
                    status.project = activeProject;
                }
                // 根据文件路径获取分支
                status.branch = await getGitBranch(filePath);
            } else if (input.cwd) {
                // 没有文件路径时用 cwd
                status.branch = await getGitBranch(input.cwd);
            }

            // 在会话开始时获取窗口句柄
            // startup: 新会话，尝试获取窗口信息
            // resume: 恢复会话，保留原有句柄
            // compact: 会话压缩，不重新捕获
            const sessionSource = input.source || '';
            let needCaptureWindow = false;

            if (hookEvent === 'SessionStart' && sessionSource === 'startup') {
                needCaptureWindow = true;
                log('SessionStart with source=startup, capturing window info...');
            } else if (hookEvent === 'SessionStart' && sessionSource === 'resume') {
                // resume 时检查是否已有窗口句柄
                log('SessionStart with source=resume, checking existing handle...');
                try {
                    const existingHandle = await new Promise((resolve) => {
                        const req = http.request({
                            hostname: '127.0.0.1',
                            port: PORT,
                            path: '/status',
                            method: 'GET',
                            family: 4,
                            timeout: 2000
                        }, (res) => {
                            let data = '';
                            res.on('data', chunk => data += chunk);
                            res.on('end', () => {
                                try {
                                    const status = JSON.parse(data);
                                    const session = status.sessions.find(s => s.id === input.session_id);
                                    resolve(session ? session.windowHandle : null);
                                } catch { resolve(null); }
                            });
                        });
                        req.on('error', () => resolve(null));
                        req.on('timeout', () => { req.destroy(); resolve(null); });
                        req.end();
                    });
                    if (!existingHandle) {
                        log('No existing handle, need to capture');
                        needCaptureWindow = true;
                    } else {
                        // 检查句柄是否仍然有效
                        try {
                            const checkValid = execSync(
                                `powershell -Command "Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public class W { [DllImport(\\"user32.dll\\")] public static extern bool IsWindow(IntPtr h); }'; [W]::IsWindow([IntPtr]::new(${existingHandle}))"`,
                                { encoding: 'utf8', timeout: 2000 }
                            );
                            if (checkValid.trim() === 'True') {
                                log('Existing handle ' + existingHandle + ' is still valid');
                            } else {
                                log('Existing handle ' + existingHandle + ' is invalid, will recapture');
                                needCaptureWindow = true;
                            }
                        } catch (e) {
                            log('Failed to validate handle: ' + e.message);
                        }
                    }
                } catch (e) {
                    log('Failed to check existing handle: ' + e.message);
                }
            } else if (hookEvent !== 'SessionStart') {
                // 非 SessionStart 事件，检查窗口句柄是否有效
                try {
                    const existingHandle = await new Promise((resolve) => {
                        const req = http.request({
                            hostname: '127.0.0.1',
                            port: PORT,
                            path: '/status',
                            method: 'GET',
                            family: 4,
                            timeout: 2000
                        }, (res) => {
                            let data = '';
                            res.on('data', chunk => data += chunk);
                            res.on('end', () => {
                                try {
                                    const status = JSON.parse(data);
                                    const session = status.sessions.find(s => s.id === input.session_id);
                                    resolve(session ? session.windowHandle : null);
                                } catch { resolve(null); }
                            });
                        });
                        req.on('error', () => resolve(null));
                        req.on('timeout', () => { req.destroy(); resolve(null); });
                        req.end();
                    });
                    if (!existingHandle) {
                        log('Session has no window handle, will capture');
                        needCaptureWindow = true;
                    } else {
                        // 检查句柄是否仍然有效
                        try {
                            const checkValid = execSync(
                                `powershell -Command "Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public class W { [DllImport(\\"user32.dll\\")] public static extern bool IsWindow(IntPtr h); }'; [W]::IsWindow([IntPtr]::new(${existingHandle}))"`,
                                { encoding: 'utf8', timeout: 2000 }
                            );
                            if (checkValid.trim() !== 'True') {
                                log('Handle ' + existingHandle + ' is invalid, will recapture');
                                needCaptureWindow = true;
                            }
                        } catch (e) {
                            log('Failed to validate handle: ' + e.message);
                            needCaptureWindow = true;
                        }
                    }
                } catch (e) { }
            }

            if (needCaptureWindow) {
                let windowHandle = null;

                // 尝试获取控制台窗口句柄
                log('Getting console window handle...');
                windowHandle = getConsoleWindowHandle();
                if (windowHandle) {
                    log('Got console window handle: ' + windowHandle);
                }

                // 如果控制台窗口获取失败，尝试进程树查找
                if (!windowHandle) {
                    log('Console window not found, trying PID tree...');
                    const targetPid = process.pid;
                    windowHandle = getWindowHandleByPid(targetPid);
                    if (windowHandle) {
                        log('Got window via PID: ' + windowHandle);
                    }
                }

                if (windowHandle) {
                    status.windowHandle = windowHandle;
                } else {
                    status.windowHandle = null;
                    log('No window handle captured');
                }
            } else if (hookEvent === 'SessionStart') {
                log('SessionStart with source=' + sessionSource + ', keeping existing window handle');
            }

            await sendStatus(status);
        } catch (e) {
            log('parse error: ' + e.message);
            await sendStatus({
                state: 'working',
                task: 'Processing...',
                message: 'Processing...'
            });
        }
    });

    if (process.stdin.isTTY) {
        clearTimeout(timeout);
        await sendStatus({
            state: 'idle',
            task: 'Ready',
            message: 'Ready'
        });
    }
}

main();