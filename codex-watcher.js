const fs = require('fs');
const path = require('path');
const http = require('http');
const { execSync } = require('child_process');

const DATA_DIR = path.join(process.env.APPDATA || process.env.HOME || process.env.USERPROFILE, 'claude-monitor');
const LOG_FILE = path.join(DATA_DIR, 'codex-watcher.log');
const DEFAULT_CODEX_SESSIONS_DIR = path.join(process.env.USERPROFILE || process.env.HOME || '', '.codex', 'sessions');
const MAX_TEXT_LENGTH = 200;
const INITIAL_FILE_LIMIT = 20;
const RECENT_FILE_AGE_MS = 2 * 60 * 60 * 1000;
const DEFAULT_RETRY_INTERVAL_MS = 5000;

function getPort() {
    return parseInt(process.env.CLAUDE_MONITOR_PORT || '18989', 10);
}

function ensureDataDir() {
    try {
        if (!fs.existsSync(DATA_DIR)) fs.mkdirSync(DATA_DIR, { recursive: true });
    } catch (e) {}
}

function log(message) {
    try {
        ensureDataDir();
        fs.appendFileSync(LOG_FILE, '[' + new Date().toISOString() + '] ' + message + '\n');
    } catch (e) {}
}

function truncate(text, maxLen) {
    if (!text) return '';
    const value = String(text).replace(/\s+/g, ' ').trim();
    if (value.length <= maxLen) return value;
    return value.substring(0, maxLen - 3) + '...';
}

function extractSessionId(filePath) {
    const name = path.basename(filePath, '.jsonl');
    return 'codex:' + name;
}

function extractText(content) {
    if (typeof content === 'string') return content;
    if (!Array.isArray(content)) return '';

    return content
        .map((block) => {
            if (!block) return '';
            if (typeof block.text === 'string') return block.text;
            if (typeof block.content === 'string') return block.content;
            return '';
        })
        .filter(Boolean)
        .join('\n');
}

function displayToolName(name) {
    if (!name) return 'tool';
    const parts = String(name).split('.');
    return parts[parts.length - 1] || 'tool';
}

function isFinalPhase(phase) {
    return phase === 'final' || phase === 'final_answer';
}

function contextFromTokenInfo(info) {
    if (!info || !info.model_context_window) return null;

    const usage = info.last_token_usage || info.total_token_usage;
    if (!usage) return null;
    const used = usage.input_tokens || 0;
    if (used <= 0) return null;

    const percentage = Math.min(100, Math.round(used / info.model_context_window * 100));
    return percentage + '%';
}

function parseCodexJsonlLine(line) {
    if (!line || !line.trim()) return null;

    let entry;
    try {
        entry = JSON.parse(line);
    } catch (e) {
        return null;
    }

    const payload = entry.payload || {};

    if (entry.type === 'session_meta') {
        return {
            kind: 'meta',
            cwd: payload.cwd || null
        };
    }

    if (entry.type === 'turn_context') {
        return {
            kind: 'meta',
            cwd: payload.cwd || null,
            model: payload.model || null,
            effort: payload.effort || null
        };
    }

    if (payload.type === 'user_message') {
        const userMessage = truncate(payload.message || extractText(payload.text_elements), MAX_TEXT_LENGTH);
        if (!userMessage) return null;

        return {
            kind: 'user',
            userMessage,
            state: 'working',
            task: 'Processing request...',
            message: 'Processing user request...'
        };
    }

    if (payload.type === 'agent_message') {
        const task = truncate(payload.message, MAX_TEXT_LENGTH);
        if (!task) return null;

        return {
            kind: 'assistant',
            state: isFinalPhase(payload.phase) ? 'complete' : 'working',
            task,
            message: isFinalPhase(payload.phase) ? 'Ready' : 'Working'
        };
    }

    if (entry.type === 'response_item' && payload.type === 'reasoning') {
        return {
            kind: 'activity',
            state: 'thinking',
            task: 'Thinking...',
            message: 'Thinking'
        };
    }

    if (entry.type === 'response_item' && payload.type === 'function_call') {
        return {
            kind: 'activity',
            state: 'working',
            task: 'Using ' + truncate(displayToolName(payload.name), 80) + '...',
            message: 'Working'
        };
    }

    if (entry.type === 'event_msg' && payload.type === 'exec_command_end') {
        const command = truncate(payload.command || 'command', 120);
        return {
            kind: 'activity',
            state: 'working',
            task: 'Ran: ' + command,
            message: 'Working'
        };
    }

    if (entry.type === 'event_msg' && payload.type === 'task_complete') {
        return {
            kind: 'activity',
            state: 'complete',
            message: 'Ready'
        };
    }

    if (entry.type === 'response_item' && payload.type === 'message' && payload.role === 'assistant') {
        const task = truncate(extractText(payload.content), MAX_TEXT_LENGTH);
        if (!task) return null;

        return {
            kind: 'assistant',
            state: isFinalPhase(payload.phase) ? 'complete' : 'working',
            task,
            message: isFinalPhase(payload.phase) ? 'Ready' : 'Working'
        };
    }

    if (payload.type === 'token_count') {
        const context = contextFromTokenInfo(payload.info);
        if (!context) return null;

        return {
            kind: 'meta',
            context
        };
    }

    return null;
}

function projectFromCwd(cwd) {
    if (!cwd) return 'codex';
    return path.basename(cwd.replace(/[\\/]$/, '')) || 'codex';
}

function getGitBranch(cwd) {
    if (!cwd || !fs.existsSync(cwd)) return null;

    try {
        const result = execSync('git branch --show-current', {
            cwd,
            encoding: 'utf8',
            timeout: 1500,
            stdio: ['ignore', 'pipe', 'ignore']
        }).trim();
        return result || null;
    } catch (e) {
        return null;
    }
}

function applyCodexEvent(base, event) {
    if (!event) return null;

    const payload = {
        sessionId: base.sessionId,
        source: 'codex',
        project: base.project || projectFromCwd(base.cwd),
        model: base.model || null,
        effort: base.effort || null,
        branch: base.branch || null
    };

    if (event.state) payload.state = event.state;
    if (event.task !== undefined) payload.task = event.task;
    if (event.message !== undefined) payload.message = event.message;
    if (event.context !== undefined) payload.context = event.context;
    if (event.userMessage !== undefined) payload.userMessage = event.userMessage;
    if (event.state === 'working') payload.progress = 50;
    if (event.state === 'complete') payload.progress = 100;

    return payload;
}

function sendStatus(data) {
    return new Promise((resolve) => {
        const body = JSON.stringify(data);
        const req = http.request({
            hostname: '127.0.0.1',
            port: getPort(),
            path: '/session',
            method: 'POST',
            headers: {
                'Content-Type': 'application/json; charset=utf-8',
                'Content-Length': Buffer.byteLength(body)
            },
            family: 4,
            timeout: 3000,
            lookup: (hostname, options, callback) => callback(null, '127.0.0.1', 4)
        }, () => resolve(true));

        req.on('error', (e) => {
            log('sendStatus error: ' + e.code + ' - ' + e.message);
            resolve(false);
        });
        req.on('timeout', () => {
            req.destroy();
            resolve(false);
        });
        req.write(body);
        req.end();
    });
}

class CodexWatcher {
    constructor(options) {
        this.sessionsDir = (options && options.sessionsDir) || DEFAULT_CODEX_SESSIONS_DIR;
        this.postStatus = (options && options.postStatus) || sendStatus;
        this.files = new Map();
        this.fileQueues = new Map();
        this.rootWatcher = null;
        this.scanTimer = null;
        this.retryTimer = null;
        this.retryIntervalMs = (options && options.retryIntervalMs) || DEFAULT_RETRY_INTERVAL_MS;
        this.running = false;
        this.startPromise = null;
    }

    start() {
        this.running = true;
        if (!fs.existsSync(this.sessionsDir)) {
            log('Codex sessions dir not found: ' + this.sessionsDir);
            this.scheduleStartRetry();
            return true;
        }

        this.startProcessing();
        log('Codex watcher started: ' + this.sessionsDir);
        return true;
    }

    scheduleStartRetry() {
        if (this.retryTimer) return;
        this.retryTimer = setInterval(() => {
            if (!this.running) return;
            if (!fs.existsSync(this.sessionsDir)) return;

            clearInterval(this.retryTimer);
            this.retryTimer = null;
            this.startProcessing();
        }, this.retryIntervalMs);
    }

    startProcessing() {
        if (this.startPromise || this.rootWatcher || this.scanTimer) return;

        this.startPromise = this.primeRecentFiles()
            .then(() => {
                if (!this.running) return;

                try {
                    this.rootWatcher = fs.watch(this.sessionsDir, { recursive: true }, (eventType, filename) => {
                        if (!filename || !String(filename).endsWith('.jsonl')) return;
                        this.processFile(path.join(this.sessionsDir, filename));
                    });
                } catch (e) {
                    log('fs.watch recursive unavailable: ' + e.message);
                }

                this.scanTimer = setInterval(() => {
                    this.scanRecentFiles(true).catch((e) => log('scanRecentFiles error: ' + e.message));
                }, 5000);
            })
            .catch((e) => log('Codex watcher startup error: ' + e.message));
    }

    stop() {
        this.running = false;
        if (this.retryTimer) clearInterval(this.retryTimer);
        if (this.rootWatcher) this.rootWatcher.close();
        if (this.scanTimer) clearInterval(this.scanTimer);
        this.retryTimer = null;
        this.rootWatcher = null;
        this.scanTimer = null;
    }

    getRecentJsonlFiles() {
        return this.listJsonlFiles()
            .map((filePath) => ({ filePath, stat: fs.statSync(filePath) }))
            .filter((item) => Date.now() - item.stat.mtimeMs <= RECENT_FILE_AGE_MS)
            .sort((a, b) => b.stat.mtimeMs - a.stat.mtimeMs)
            .slice(0, INITIAL_FILE_LIMIT);
    }

    async primeRecentFiles() {
        const files = this.getRecentJsonlFiles();
        for (const item of files) {
            await this.processFile(item.filePath, true, { emitStatus: false });
        }
    }

    async scanRecentFiles(processFromStart) {
        const files = this.getRecentJsonlFiles();
        for (const item of files) {
            await this.processFile(item.filePath, processFromStart);
        }
    }

    listJsonlFiles() {
        const results = [];
        const walk = (dir) => {
            let entries = [];
            try {
                entries = fs.readdirSync(dir, { withFileTypes: true });
            } catch (e) {
                return;
            }

            for (const entry of entries) {
                const fullPath = path.join(dir, entry.name);
                if (entry.isDirectory()) {
                    walk(fullPath);
                } else if (entry.isFile() && entry.name.endsWith('.jsonl')) {
                    results.push(fullPath);
                }
            }
        };

        walk(this.sessionsDir);
        return results;
    }

    getFileState(filePath, processFromStart) {
        if (!this.files.has(filePath)) {
            const offset = processFromStart === false && fs.existsSync(filePath) ? fs.statSync(filePath).size : 0;
            this.files.set(filePath, {
                offset,
                base: {
                    sessionId: extractSessionId(filePath),
                    project: 'codex',
                    model: null,
                    effort: null,
                    branch: null,
                    cwd: null
                },
                buffer: ''
            });
        }
        return this.files.get(filePath);
    }

    processFile(filePath, processFromStart, options) {
        if (!filePath) return Promise.resolve();

        const previous = this.fileQueues.get(filePath) || Promise.resolve();
        const current = previous
            .catch(() => {})
            .then(() => this.processFileNow(filePath, processFromStart, options));

        this.fileQueues.set(filePath, current);
        current.catch((e) => log('processFile error: ' + e.message));
        const cleanup = () => {
            if (this.fileQueues.get(filePath) === current) {
                this.fileQueues.delete(filePath);
            }
        };
        current.then(cleanup, cleanup);

        return current;
    }

    async processFileNow(filePath, processFromStart, options) {
        if (!filePath || !filePath.endsWith('.jsonl') || !fs.existsSync(filePath)) return;

        const emitStatus = !options || options.emitStatus !== false;
        const state = this.getFileState(filePath, processFromStart);
        const stat = fs.statSync(filePath);
        if (stat.size < state.offset) state.offset = 0;
        if (stat.size === state.offset) return;

        const stream = fs.createReadStream(filePath, {
            encoding: 'utf8',
            start: state.offset,
            end: stat.size - 1
        });

        let chunk = '';
        for await (const part of stream) {
            chunk += part;
        }

        state.offset = stat.size;
        const text = state.buffer + chunk;
        const lines = text.split(/\r?\n/);
        state.buffer = lines.pop() || '';

        for (const line of lines) {
            const event = parseCodexJsonlLine(line);
            if (!event) continue;

            if (event.kind === 'meta') {
                if (event.cwd) {
                    state.base.cwd = event.cwd;
                    state.base.project = projectFromCwd(event.cwd);
                    state.base.branch = getGitBranch(event.cwd);
                }
                if (event.model) state.base.model = event.model;
                if (event.effort) state.base.effort = event.effort;
                if (event.context && emitStatus) {
                    const payload = applyCodexEvent(state.base, event);
                    await this.postStatus(payload);
                }
                continue;
            }

            if (!emitStatus) continue;
            const payload = applyCodexEvent(state.base, event);
            await this.postStatus(payload);
        }
    }
}

function startWatcher() {
    const watcher = new CodexWatcher();
    watcher.start();
    return watcher;
}

if (require.main === module) {
    startWatcher();
    process.stdin.resume();
}

module.exports = {
    CodexWatcher,
    applyCodexEvent,
    contextFromTokenInfo,
    extractSessionId,
    getPort,
    parseCodexJsonlLine,
    startWatcher
};
