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

const PORT = parseInt(process.env.CLAUDE_MONITOR_PORT || '18989', 10);
const HOOK_DIR = __dirname;
const DATA_DIR = process.env.APPDATA || process.env.HOME;
const DEBUG_FILE = path.join(DATA_DIR, 'claude-monitor', 'hook-debug.log');
const WINDOW_HANDLES_FILE = path.join(DATA_DIR, 'claude-monitor', 'window-handles.json');

// 终端窗口类名常量
const TERMINAL_WINDOW_CLASSES = ['CASCADIA_HOSTING_WINDOW_CLASS', 'ConsoleWindowClass', 'PseudoConsoleWindow'];

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

// 获取当前前台窗口句柄，并验证是否是终端窗口（异步版本）
async function getForegroundWindow() {
    log('getForegroundWindow: starting...');
    return new Promise((resolve) => {
        if (process.platform !== 'win32') {
            log('getForegroundWindow: not win32 platform');
            resolve(null);
            return;
        }
        // 使用脚本文件获取前台窗口句柄和类名
        const scriptPath = path.join(HOOK_DIR, 'get-foreground-window.ps1');
        if (!fs.existsSync(scriptPath)) {
            log('getForegroundWindow: script not found');
            resolve(null);
            return;
        }

        log('getForegroundWindow: spawning PowerShell...');
        const child = spawn('powershell', [
            '-ExecutionPolicy', 'Bypass',
            '-File', scriptPath
        ], {
            windowsHide: true
        });

        let stdout = '';
        let stderr = '';
        let timedOut = false;

        child.stdout.on('data', (data) => {
            stdout += data.toString();
        });

        child.stderr.on('data', (data) => {
            stderr += data.toString();
        });

        // 设置超时强制杀死进程
        const timeoutId = setTimeout(() => {
            timedOut = true;
            log('getForegroundWindow: timeout, killing PowerShell...');
            child.kill();
        }, 3000);

        child.on('close', (code) => {
            clearTimeout(timeoutId);
            if (timedOut) {
                log('getForegroundWindow: PowerShell killed due to timeout');
                resolve(null);
                return;
            }

            const output = stdout.trim();
            log('getForegroundWindow: PowerShell returned: ' + output);

            if (!output) {
                log('getForegroundWindow: empty output, stderr: ' + stderr.trim());
                resolve(null);
                return;
            }
            if (output.startsWith('Error:')) {
                log('getForegroundWindow: script error: ' + output);
                resolve(null);
                return;
            }
            const parts = output.split('|');
            log('getForegroundWindow: parts count=' + parts.length);
            if (parts.length >= 2) {
                const handle = parts[0];
                const className = parts[1];
                // 只接受终端窗口类名
                if (TERMINAL_WINDOW_CLASSES.includes(className)) {
                    log('getForegroundWindow: terminal window found: handle=' + handle + ' class=' + className);
                    resolve(handle);
                    return;
                }
                log('getForegroundWindow: NOT terminal: handle=' + handle + ' class=' + className);
            }
            log('getForegroundWindow: unexpected output format: ' + output);
            resolve(null);
        });

        child.on('error', (err) => {
            clearTimeout(timeoutId);
            log('getForegroundWindow: spawn error: ' + err.message);
            resolve(null);
        });
    });
}

// 保存窗口句柄到文件（直接覆盖）
function saveWindowHandle(sessionId, windowHandle) {
    try {
        const dir = path.dirname(WINDOW_HANDLES_FILE);
        fs.mkdirSync(dir, { recursive: true });  // 直接创建，不检查存在

        let handles = {};
        if (fs.existsSync(WINDOW_HANDLES_FILE)) {
            try {
                handles = JSON.parse(fs.readFileSync(WINDOW_HANDLES_FILE, 'utf8'));
            } catch { }
        }

        handles[sessionId] = {
            handle: windowHandle,
            time: Date.now()
        };

        // 清理超过 1 周的记录
        const oneWeekAgo = Date.now() - 7 * 24 * 3600000;
        for (const key in handles) {
            if (handles[key].time < oneWeekAgo) {
                delete handles[key];
            }
        }

        fs.writeFileSync(WINDOW_HANDLES_FILE, JSON.stringify(handles, null, 2));
        log('Saved window handle for session ' + sessionId + ': ' + windowHandle);
    } catch (e) {
        log('saveWindowHandle error: ' + e.message);
    }
}

// 加载已保存的窗口句柄
function loadWindowHandle(sessionId) {
    try {
        if (fs.existsSync(WINDOW_HANDLES_FILE)) {
            const handles = JSON.parse(fs.readFileSync(WINDOW_HANDLES_FILE, 'utf8'));
            if (handles[sessionId] && handles[sessionId].handle) {
                // 检查句柄是否仍然有效，并且是终端窗口
                const checkResult = execSync(
                    `powershell -Command "$code='[DllImport(\\"user32.dll\\")] public static extern bool IsWindow(IntPtr hWnd); [DllImport(\\"user32.dll\\",CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd,char[] lpClassName,int nMaxCount);'; $t=Add-Type -MemberDefinition $code -Name ('CHK'+(Get-Random)) -Namespace 'Win32CHK' -PassThru; if(!$t::IsWindow([IntPtr]${handles[sessionId].handle})){Write-Output 'Invalid'}else{$chars=New-Object char[] 256; $len=$t::GetClassName([IntPtr]${handles[sessionId].handle},$chars,256); $class=-join $chars[0..($len-1)]; Write-Output $class}"`,
                    { encoding: 'utf8', timeout: 3000 }
                ).trim();
                if (checkResult === 'Invalid') {
                    log('Saved window handle ' + handles[sessionId].handle + ' is no longer valid');
                } else {
                    // 检查窗口类名是否是终端
                    if (TERMINAL_WINDOW_CLASSES.includes(checkResult)) {
                        log('Loaded valid terminal window handle for session ' + sessionId + ': ' + handles[sessionId].handle + ' class=' + checkResult);
                        return handles[sessionId].handle;
                    } else {
                        log('Saved window handle ' + handles[sessionId].handle + ' is not a terminal window, class=' + checkResult);
                    }
                }
            }
        }
    } catch (e) {
        log('loadWindowHandle error: ' + e.message);
    }
    return null;
}

// Check if session needs handle recapture
async function needsHandleRecapture(sessionId) {
    return new Promise((resolve) => {
        if (!sessionId) {
            resolve(false);
            return;
        }
        const req = http.request({
            hostname: '127.0.0.1',
            port: PORT,
            path: '/session/' + sessionId + '/needs-recapture',
            method: 'GET',
            family: 4,
            timeout: 3000,
            lookup: (hostname, options, callback) => {
                callback(null, '127.0.0.1', 4);
            }
        }, (res) => {
            let data = '';
            res.on('data', chunk => data += chunk);
            res.on('end', () => {
                try {
                    const result = JSON.parse(data);
                    log('needsHandleRecapture: sessionId=' + sessionId + ', result=' + result.needsHandleRecapture);
                    resolve(result.needsHandleRecapture || false);
                } catch (e) {
                    log('needsHandleRecapture parse error: ' + e.message);
                    resolve(false);
                }
            });
        });
        req.on('error', (e) => {
            log('needsHandleRecapture error: ' + e.code);
            resolve(false);
        });
        req.on('timeout', () => { req.destroy(); resolve(false); });
        req.end();
    });
}

// Clear recapture flag after successful capture
async function clearHandleRecapture(sessionId) {
    return new Promise((resolve) => {
        if (!sessionId) {
            resolve(false);
            return;
        }
        const req = http.request({
            hostname: '127.0.0.1',
            port: PORT,
            path: '/session/' + sessionId + '/clear-recapture',
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            family: 4,
            timeout: 3000,
            lookup: (hostname, options, callback) => {
                callback(null, '127.0.0.1', 4);
            }
        }, (res) => {
            res.on('data', () => {});
            res.on('end', () => {
                log('clearHandleRecapture: cleared for ' + sessionId);
                resolve(true);
            });
        });
        req.on('error', (e) => {
            log('clearHandleRecapture error: ' + e.code);
            resolve(false);
        });
        req.on('timeout', () => { req.destroy(); resolve(false); });
        req.write('{}');
        req.end();
    });
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
            let data = '';
            log('checkServerRunning: got response ' + res.statusCode);
            res.on('data', chunk => data += chunk);
            res.on('end', () => {
                if (res.statusCode !== 200) {
                    resolve(false);
                    return;
                }
                try {
                    const parsed = JSON.parse(data);
                    resolve(parsed && parsed.status === 'ok');
                } catch (e) {
                    resolve(false);
                }
            });
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

// 带重试的服务器检测（减少重试次数，快速失败）
async function checkServerRunningWithRetry(maxRetries = 2) {
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
        // 通过 PowerShell 执行 tasklist，避免 Git Bash 转义问题
        const result = execSync(
            `powershell -Command "tasklist /FI 'PID eq ${pid}' /NH"`,
            { encoding: 'utf8', timeout: 3000 }
        );
        // tasklist 返回格式: "Monitor.exe                 10608 Console                    1     83,912 K"
        // 或 "INFO: No tasks are running which match the specified criteria."
        const exists = result.includes(String(pid)) && !result.includes('No tasks') && !result.includes('INFO:');
        log('checkProcessExists: pid=' + pid + ', exists=' + exists);
        return exists;
    } catch (e) {
        log('checkProcessExists error: pid=' + pid + ', error=' + e.message);
        return false;
    }
}

async function ensureMonitorRunning() {
    try {
        const serverRunning = await checkServerRunningWithRetry(2);
        if (!serverRunning) {
            log('Server not running, starting...');
            await startServerAndMonitor();
            return;
        }

        // Server正在运行，不再检查Monitor状态
        // Server会自己管理Monitor进程（每5秒检查心跳）
        log('Server running, Monitor managed by server');
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

    // Notification hook - 处理权限请求和空闲提示
    if (hookEvent === 'Notification') {
        const notificationType = input.type || '';
        const notificationMessage = input.message || '';

        log('Notification event: type=' + notificationType + ', message=' + notificationMessage.substring(0, 50));

        if (notificationType === 'permission_prompt') {
            // 权限请求 - 使用 waiting 状态（复用现有样式）
            const permissionText = truncate(notificationMessage, 100);
            return {
                state: 'waiting',
                task: '⚠️ 权限请求: ' + permissionText,
                message: 'Permission required',
                model: model,
                context: context,
                branch: null
            };
        } else if (notificationType === 'idle_prompt') {
            // 空闲提示 - 保持当前状态或设为 idle
            return {
                state: 'idle',
                task: '💤 等待输入...',
                message: 'Idle',
                model: model,
                context: context,
                branch: null
            };
        }
        // 其他通知类型，默认处理
        return {
            state: 'idle',
            task: notificationMessage || 'Notification',
            message: 'Notification',
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

    // 简化启动逻辑：只检查Server，让Server管理Monitor
    const serverRunning = await checkServerRunningWithRetry(2);
    if (!serverRunning) {
        log('Server not running, starting...');
        await startServerAndMonitor();
        // Wait for server to be ready
        await new Promise(r => setTimeout(r, 1000));
    }

    // Read from stdin for proper hook mode
    let inputData = '';

    // Quick timeout for cases where stdin is empty - just exit without creating unknown session
    const timeout = setTimeout(async () => {
        log('stdin timeout, exiting without status update');
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

            // 窗口句柄捕获优先级：
            // 1. SessionStart (startup/resume) 时的前台窗口（优先，因为用户正在操作终端）
            // 2. 已保存的有效终端窗口句柄（resume 时使用）
            // 3. UserPromptSubmit 时的前台窗口（后备，可能有延迟导致窗口已切换）
            //
            // 注意：UserPromptSubmit 时模型可能有思考延迟，用户可能已切换窗口，
            // 所以优先使用 SessionStart 时捕获的窗口

            const sessionSource = input.source || '';

            // SessionStart 时的处理
            if (hookEvent === 'SessionStart') {
                // 检查是否需要重新绑定
                const needsRecapture = await needsHandleRecapture(input.session_id);

                if (needsRecapture) {
                    // 用户标记了窗口绑定错误，强制捕获当前前台窗口
                    log('SessionStart: session marked for recapture, forcing foreground capture...');
                    const windowHandle = await getForegroundWindow();
                    if (windowHandle) {
                        log('SessionStart: captured terminal window for recapture: ' + windowHandle);
                        status.windowHandle = windowHandle;
                        saveWindowHandle(input.session_id, windowHandle);
                        await clearHandleRecapture(input.session_id);
                    } else {
                        log('SessionStart: foreground window is not terminal, keeping old handle');
                        // 保持旧句柄，等待下次机会
                        const savedHandle = loadWindowHandle(input.session_id);
                        if (savedHandle) {
                            status.windowHandle = savedHandle;
                        }
                    }
                } else {
                    // 正常流程：首先尝试加载已保存的句柄
                    const savedHandle = loadWindowHandle(input.session_id);
                    if (savedHandle) {
                        status.windowHandle = savedHandle;
                        log('SessionStart: using saved terminal handle: ' + savedHandle);
                    } else if (sessionSource === 'startup' || sessionSource === 'resume' || sessionSource === 'clear') {
                        // 没有有效保存句柄，尝试捕获当前前台窗口
                        // startup: 新启动
                        // resume: 从历史恢复
                        // clear: /clear 清除后重新开始
                        log('SessionStart (' + sessionSource + '): no saved handle, capturing foreground window...');
                        const windowHandle = await getForegroundWindow();
                        if (windowHandle) {
                            log('SessionStart: captured terminal window: ' + windowHandle);
                            status.windowHandle = windowHandle;
                            saveWindowHandle(input.session_id, windowHandle);
                        } else {
                            log('SessionStart: foreground window is not terminal, will retry at UserPromptSubmit');
                            status.windowHandle = null;
                        }
                    } else {
                        log('SessionStart (' + sessionSource + '): compact/other, no handle capture');
                    }
                }
            }

            // UserPromptSubmit: 检查是否需要重新绑定
            if (hookEvent === 'UserPromptSubmit') {
                const needsRecapture = await needsHandleRecapture(input.session_id);

                if (needsRecapture) {
                    // 用户标记了窗口绑定错误，强制捕获当前前台窗口
                    log('UserPromptSubmit: session marked for recapture, forcing foreground capture...');
                    const windowHandle = await getForegroundWindow();
                    if (windowHandle) {
                        log('UserPromptSubmit: captured terminal window for recapture: ' + windowHandle);
                        status.windowHandle = windowHandle;
                        saveWindowHandle(input.session_id, windowHandle);
                        await clearHandleRecapture(input.session_id);
                    } else {
                        log('UserPromptSubmit: foreground window is not terminal, will retry later');
                        // 不清除标记，等待下次 SessionStart
                    }
                } else {
                    // 正常流程：使用已保存的句柄，如果没有则尝试捕获
                    const savedHandle = loadWindowHandle(input.session_id);
                    if (savedHandle) {
                        status.windowHandle = savedHandle;
                        log('UserPromptSubmit: using saved terminal handle: ' + savedHandle);
                    } else {
                        // 没有保存的句柄，尝试捕获当前前台窗口
                        // 用户正在输入，前台窗口应该是终端
                        log('UserPromptSubmit: no saved handle, trying to capture foreground window...');
                        const windowHandle = await getForegroundWindow();
                        if (windowHandle) {
                            log('UserPromptSubmit: captured terminal window: ' + windowHandle);
                            status.windowHandle = windowHandle;
                            saveWindowHandle(input.session_id, windowHandle);
                        } else {
                            log('UserPromptSubmit: foreground window is not terminal, keeping null');
                        }
                    }
                }
            }

            await sendStatus(status);
        } catch (e) {
            log('parse error: ' + e.message);
            // Don't send status on parse error to avoid creating unknown session
        }
    });

    if (process.stdin.isTTY) {
        clearTimeout(timeout);
        // Don't send status when stdin is TTY to avoid creating unknown session
        log('stdin is TTY, exiting without status update');
        process.exit(0);
    }
}

if (require.main === module) {
    main();
}

module.exports = {
    checkServerRunning
};
