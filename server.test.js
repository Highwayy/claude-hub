const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');

function tempDir(prefix) {
    return fs.mkdtempSync(path.join(os.tmpdir(), prefix));
}

function request(port, method, pathname, body) {
    return new Promise((resolve, reject) => {
        const data = body ? JSON.stringify(body) : '';
        const req = http.request({
            hostname: '127.0.0.1',
            port,
            path: pathname,
            method,
            headers: {
                'Content-Type': 'application/json',
                'Content-Length': Buffer.byteLength(data)
            },
            timeout: 3000
        }, (res) => {
            let text = '';
            res.on('data', (chunk) => text += chunk);
            res.on('end', () => {
                try {
                    resolve({ statusCode: res.statusCode, body: JSON.parse(text || '{}') });
                } catch (e) {
                    resolve({ statusCode: res.statusCode, body: text });
                }
            });
        });
        req.on('error', reject);
        req.on('timeout', () => {
            req.destroy(new Error('request timeout'));
        });
        if (data) req.write(data);
        req.end();
    });
}

async function waitForServer(port) {
    const deadline = Date.now() + 5000;
    while (Date.now() < deadline) {
        try {
            const result = await request(port, 'GET', '/health');
            if (result.statusCode === 200 && result.body.status === 'ok') return;
        } catch (e) {}
        await new Promise((resolve) => setTimeout(resolve, 100));
    }
    throw new Error('server did not start');
}

async function startServer(appData, port) {
    const child = spawn(process.execPath, ['server.js'], {
        cwd: __dirname,
        env: {
            ...process.env,
            APPDATA: appData,
            CLAUDE_MONITOR_PORT: String(port),
            CLAUDE_MONITOR_DISABLE_AUTOSTART: '1'
        },
        stdio: 'ignore',
        windowsHide: true
    });
    await waitForServer(port);
    return child;
}

function stopServer(child) {
    return new Promise((resolve) => {
        child.once('exit', resolve);
        child.kill();
        setTimeout(resolve, 1000);
    });
}

test('deleted sessions stay deleted after server restart', async () => {
    const appData = tempDir('claude-monitor-server-');
    const port = 20000 + Math.floor(Math.random() * 20000);

    let child = await startServer(appData, port);
    await request(port, 'POST', '/session', {
        sessionId: 'delete-me',
        project: 'demo',
        state: 'working',
        task: 'temporary'
    });
    await request(port, 'DELETE', '/session/delete-me');
    await stopServer(child);

    child = await startServer(appData, port);
    const debug = await request(port, 'GET', '/debug');
    assert.equal(debug.body.sessions['delete-me'], undefined);
    assert.equal(debug.body.activeSessionIds.includes('delete-me'), false);
    await stopServer(child);
});

test('reset stays empty after server restart', async () => {
    const appData = tempDir('claude-monitor-server-');
    const port = 20000 + Math.floor(Math.random() * 20000);

    let child = await startServer(appData, port);
    await request(port, 'POST', '/session', {
        sessionId: 'reset-me',
        project: 'demo',
        state: 'working',
        task: 'temporary'
    });
    await request(port, 'POST', '/reset');
    await stopServer(child);

    child = await startServer(appData, port);
    const debug = await request(port, 'GET', '/debug');
    assert.deepEqual(debug.body.sessions, {});
    assert.deepEqual(debug.body.activeSessionIds, []);
    await stopServer(child);
});
