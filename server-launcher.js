const fs = require('fs');
const path = require('path');
const { spawn } = require('child_process');

const ROOT_DIR = __dirname;
const DATA_DIR = path.join(process.env.APPDATA || process.env.HOME || process.env.USERPROFILE, 'claude-monitor');
const LOG_FILE = path.join(DATA_DIR, 'server-launcher.log');

function log(message) {
    try {
        if (!fs.existsSync(DATA_DIR)) fs.mkdirSync(DATA_DIR, { recursive: true });
        fs.appendFileSync(LOG_FILE, `[${new Date().toISOString()}] pid=${process.pid} ${message}\n`);
    } catch (e) {}
}

try {
    const serverPath = path.join(ROOT_DIR, 'server.js');
    const child = spawn(process.execPath, [serverPath], {
        cwd: ROOT_DIR,
        detached: true,
        stdio: 'ignore',
        windowsHide: true
    });
    child.unref();
    log(`started server pid=${child.pid}`);
} catch (e) {
    log(`failed ${e.stack || e.message}`);
    process.exit(1);
}
