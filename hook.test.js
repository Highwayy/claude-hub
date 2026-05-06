const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');

function listen(handler) {
    return new Promise((resolve) => {
        const server = http.createServer(handler);
        server.listen(0, '127.0.0.1', () => {
            resolve({ server, port: server.address().port });
        });
    });
}

function loadHookForPort(port) {
    process.env.CLAUDE_MONITOR_PORT = String(port);
    delete require.cache[require.resolve('./hook')];
    return require('./hook');
}

test('checkServerRunning rejects non-monitor health responses', async () => {
    const { server, port } = await listen((req, res) => {
        res.statusCode = 404;
        res.end('not monitor');
    });

    try {
        const { checkServerRunning } = loadHookForPort(port);
        assert.equal(await checkServerRunning(), false);
    } finally {
        server.close();
    }
});

test('checkServerRunning accepts monitor health response', async () => {
    const { server, port } = await listen((req, res) => {
        res.setHeader('Content-Type', 'application/json');
        res.end(JSON.stringify({ status: 'ok', sessions: 0 }));
    });

    try {
        const { checkServerRunning } = loadHookForPort(port);
        assert.equal(await checkServerRunning(), true);
    } finally {
        server.close();
    }
});

test('buildDetachedServerLaunch starts the Node launcher', () => {
    const { buildDetachedServerLaunch } = loadHookForPort(18989);
    const launch = buildDetachedServerLaunch(
        'D:\\Projects\\claude-monitor\\server.js',
        'D:\\Projects\\claude-monitor'
    );

    assert.equal(launch.fileName, process.execPath);
    assert.equal(launch.options.detached, true);
    assert.match(launch.args.join(' '), /server-launcher\.js/);
});
