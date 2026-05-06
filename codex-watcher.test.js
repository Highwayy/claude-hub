const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const {
    CodexWatcher,
    extractSessionId,
    parseCodexJsonlLine,
    applyCodexEvent,
    contextFromTokenInfo
} = require('./codex-watcher');

function writeJsonl(filePath, entries) {
    fs.writeFileSync(filePath, entries.map((entry) => JSON.stringify(entry)).join('\n') + '\n');
}

function makeTempSessionsDir() {
    return fs.mkdtempSync(path.join(os.tmpdir(), 'codex-watcher-test-'));
}

test('extractSessionId keeps rollout id distinct from Claude sessions', () => {
    const file = path.join('C:\\Users\\xhw\\.codex\\sessions\\2026\\05\\01', 'rollout-2026-05-01T11-12-43-019de186-3b79-7660-b9f1-d887a138793c.jsonl');

    assert.equal(
        extractSessionId(file),
        'codex:rollout-2026-05-01T11-12-43-019de186-3b79-7660-b9f1-d887a138793c'
    );
});

test('parseCodexJsonlLine maps user messages', () => {
    const line = JSON.stringify({
        timestamp: '2026-05-01T03:13:19.652Z',
        type: 'event_msg',
        payload: {
            type: 'user_message',
            message: 'please wire Codex sessions in'
        }
    });

    assert.deepEqual(parseCodexJsonlLine(line), {
        kind: 'user',
        userMessage: 'please wire Codex sessions in',
        state: 'working',
        task: 'Processing request...',
        message: 'Processing user request...',
        eventTimestamp: 1777605199652
    });
});

test('parseCodexJsonlLine maps assistant commentary messages', () => {
    const line = JSON.stringify({
        timestamp: '2026-05-01T03:15:04.290Z',
        type: 'event_msg',
        payload: {
            type: 'agent_message',
            message: 'I will add a Codex watcher.',
            phase: 'commentary'
        }
    });

    assert.deepEqual(parseCodexJsonlLine(line), {
        kind: 'assistant',
        state: 'working',
        task: 'I will add a Codex watcher.',
        message: 'Working',
        eventTimestamp: 1777605304290
    });
});

test('parseCodexJsonlLine maps reasoning to thinking state', () => {
    const line = JSON.stringify({
        timestamp: '2026-05-01T03:15:04.290Z',
        type: 'response_item',
        payload: {
            type: 'reasoning',
            summary: []
        }
    });

    assert.deepEqual(parseCodexJsonlLine(line), {
        kind: 'activity',
        state: 'thinking',
        task: 'Thinking...',
        message: 'Thinking',
        eventTimestamp: 1777605304290
    });
});

test('parseCodexJsonlLine maps function calls to working state', () => {
    const line = JSON.stringify({
        timestamp: '2026-05-01T03:15:04.290Z',
        type: 'response_item',
        payload: {
            type: 'function_call',
            name: 'functions.shell_command',
            arguments: '{}',
            call_id: 'call_123'
        }
    });

    assert.deepEqual(parseCodexJsonlLine(line), {
        kind: 'activity',
        state: 'working',
        task: 'Using shell_command...',
        message: 'Working',
        eventTimestamp: 1777605304290
    });
});

test('parseCodexJsonlLine maps command results to working state with command text', () => {
    const line = JSON.stringify({
        timestamp: '2026-05-01T03:15:04.290Z',
        type: 'event_msg',
        payload: {
            type: 'exec_command_end',
            command: 'npm test',
            exit_code: 0
        }
    });

    assert.deepEqual(parseCodexJsonlLine(line), {
        kind: 'activity',
        state: 'working',
        task: 'Ran: npm test',
        message: 'Working',
        eventTimestamp: 1777605304290
    });
});

test('parseCodexJsonlLine maps final assistant response items to complete', () => {
    const line = JSON.stringify({
        timestamp: '2026-05-01T03:15:04.290Z',
        type: 'response_item',
        payload: {
            type: 'message',
            role: 'assistant',
            content: [{ type: 'output_text', text: 'Codex watcher is connected.' }],
            phase: 'final_answer'
        }
    });

    assert.deepEqual(parseCodexJsonlLine(line), {
        kind: 'assistant',
        state: 'complete',
        task: 'Codex watcher is connected.',
        message: 'Ready',
        eventTimestamp: 1777605304290
    });
});

test('parseCodexJsonlLine maps task completion event to complete', () => {
    const line = JSON.stringify({
        timestamp: '2026-05-01T03:15:04.290Z',
        type: 'event_msg',
        payload: {
            type: 'task_complete'
        }
    });

    assert.deepEqual(parseCodexJsonlLine(line), {
        kind: 'activity',
        state: 'complete',
        message: 'Ready',
        eventTimestamp: 1777605304290
    });
});

test('parseCodexJsonlLine maps turn context metadata including effort', () => {
    const line = JSON.stringify({
        timestamp: '2026-05-01T03:13:19.652Z',
        type: 'turn_context',
        payload: {
            cwd: 'D:\\Projects\\claude-monitor',
            model: 'gpt-5.5',
            effort: 'high'
        }
    });

    assert.deepEqual(parseCodexJsonlLine(line), {
        kind: 'meta',
        cwd: 'D:\\Projects\\claude-monitor',
        model: 'gpt-5.5',
        effort: 'high',
        eventTimestamp: 1777605199652
    });
});

test('contextFromTokenInfo uses last input tokens without double-counting cached tokens', () => {
    assert.equal(contextFromTokenInfo({
        total_token_usage: {
            input_tokens: 90000,
            cached_input_tokens: 50000,
            output_tokens: 2000
        },
        last_token_usage: {
            input_tokens: 10000,
            cached_input_tokens: 5000,
            output_tokens: 2000
        },
        model_context_window: 100000
    }), '10%');
});

test('applyCodexEvent merges event data into session payload', () => {
    const base = {
        sessionId: 'codex:rollout-demo',
        project: 'claude-monitor',
        model: 'gpt-5.5',
        effort: 'high',
        branch: 'main'
    };

    const payload = applyCodexEvent(base, {
        kind: 'user',
        state: 'working',
        task: 'Processing request...',
        message: 'Processing user request...',
        userMessage: 'add codex sessions'
    });

    assert.deepEqual(payload, {
        sessionId: 'codex:rollout-demo',
        source: 'codex',
        project: 'claude-monitor',
        model: 'gpt-5.5',
        effort: 'high',
        branch: 'main',
        state: 'working',
        task: 'Processing request...',
        message: 'Processing user request...',
        progress: 50,
        userMessage: 'add codex sessions',
        eventKind: 'user'
    });
});

test('startup primes recent files without replaying old session events', async () => {
    const dir = makeTempSessionsDir();
    const filePath = path.join(dir, 'rollout-old.jsonl');
    const posts = [];

    writeJsonl(filePath, [
        { type: 'turn_context', payload: { cwd: dir, model: 'gpt-5.5', effort: 'high' } },
        { type: 'event_msg', payload: { type: 'user_message', message: 'old request' } },
        { type: 'event_msg', payload: { type: 'task_complete' } }
    ]);

    const watcher = new CodexWatcher({
        sessionsDir: dir,
        postStatus: async (payload) => posts.push(payload)
    });

    await watcher.primeRecentFiles();
    assert.equal(posts.length, 0);

    fs.appendFileSync(filePath, JSON.stringify({
        type: 'event_msg',
        payload: { type: 'user_message', message: 'new request' }
    }) + '\n');

    await watcher.processFile(filePath);
    assert.equal(posts.length, 1);
    assert.equal(posts[0].userMessage, 'new request');
    assert.equal(posts[0].model, 'gpt-5.5');
    assert.equal(posts[0].effort, 'high');
    assert.equal(posts[0].project, path.basename(dir));
});

test('periodic scan reads newly discovered files from the beginning', async () => {
    const dir = makeTempSessionsDir();
    const filePath = path.join(dir, 'rollout-new.jsonl');
    const posts = [];

    writeJsonl(filePath, [
        { type: 'turn_context', payload: { cwd: dir, model: 'gpt-5.5', effort: 'medium' } },
        { type: 'event_msg', payload: { type: 'user_message', message: 'first request' } }
    ]);

    const watcher = new CodexWatcher({
        sessionsDir: dir,
        postStatus: async (payload) => posts.push(payload)
    });

    await watcher.scanRecentFiles(true);
    assert.equal(posts.length, 1);
    assert.equal(posts[0].userMessage, 'first request');
    assert.equal(posts[0].model, 'gpt-5.5');
    assert.equal(posts[0].effort, 'medium');
    assert.equal(posts[0].project, path.basename(dir));
});

test('processFile serializes concurrent reads for the same file', async () => {
    const dir = makeTempSessionsDir();
    const filePath = path.join(dir, 'rollout-concurrent.jsonl');
    const posts = [];
    let firstPostBlocked = true;
    let releaseFirstPost;
    const firstPostReleased = new Promise((resolve) => {
        releaseFirstPost = resolve;
    });

    writeJsonl(filePath, [
        { type: 'turn_context', payload: { cwd: dir, model: 'gpt-5.5', effort: 'medium' } },
        { type: 'event_msg', payload: { type: 'user_message', message: 'first request' } }
    ]);

    const watcher = new CodexWatcher({
        sessionsDir: dir,
        postStatus: async (payload) => {
            posts.push(payload);
            if (firstPostBlocked) {
                firstPostBlocked = false;
                await firstPostReleased;
            }
        }
    });

    const firstRead = watcher.processFile(filePath);
    await new Promise((resolve) => setTimeout(resolve, 50));
    const secondRead = watcher.processFile(filePath);

    releaseFirstPost();
    await Promise.all([firstRead, secondRead]);

    assert.equal(posts.length, 1);
    assert.equal(posts[0].userMessage, 'first request');
});

test('start waits for priming before enabling live scans', async () => {
    const dir = makeTempSessionsDir();
    const watcher = new CodexWatcher({ sessionsDir: dir, postStatus: async () => {} });
    let releasePrime;
    const primeReleased = new Promise((resolve) => {
        releasePrime = resolve;
    });

    watcher.primeRecentFiles = async () => {
        await primeReleased;
    };

    assert.equal(watcher.start(), true);
    assert.equal(watcher.scanTimer, null);

    releasePrime();
    await watcher.startPromise;

    assert.notEqual(watcher.scanTimer, null);
    watcher.stop();
});

test('start retries until the Codex sessions directory exists', async () => {
    const dir = path.join(makeTempSessionsDir(), 'missing-sessions');
    const watcher = new CodexWatcher({
        sessionsDir: dir,
        retryIntervalMs: 20,
        postStatus: async () => {}
    });

    assert.equal(watcher.start(), true);
    assert.notEqual(watcher.retryTimer, null);
    assert.equal(watcher.running, true);

    fs.mkdirSync(dir, { recursive: true });
    await new Promise((resolve) => setTimeout(resolve, 80));

    assert.equal(watcher.retryTimer, null);
    assert.notEqual(watcher.startPromise, null);
    watcher.stop();
});

test('sendStatus uses configured monitor port', async () => {
    process.env.CLAUDE_MONITOR_PORT = '23456';
    delete require.cache[require.resolve('./codex-watcher')];
    const { getPort } = require('./codex-watcher');

    assert.equal(getPort(), 23456);

    delete process.env.CLAUDE_MONITOR_PORT;
    delete require.cache[require.resolve('./codex-watcher')];
});
