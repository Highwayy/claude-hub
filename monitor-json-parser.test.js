const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');

function extractMonitorMethod(name) {
    const source = fs.readFileSync('Monitor.cs', 'utf8');
    const signature = 'private string ' + name;
    const start = source.indexOf(signature);
    assert.notEqual(start, -1, name + ' not found');

    const braceStart = source.indexOf('{', start);
    let depth = 0;
    for (let i = braceStart; i < source.length; i++) {
        if (source[i] === '{') depth++;
        else if (source[i] === '}') {
            depth--;
            if (depth === 0) return source.substring(start, i + 1);
        }
    }
    throw new Error(name + ' end not found');
}

function portMethodToJavaScript(methodSource) {
    let js = methodSource
        .replace(/private string GetJsonString\(string json, string key\)/, 'function GetJsonString(json, key)')
        .replace(/private string DecodeJsonString\(string s\)/, 'function DecodeJsonString(s)')
        .replace(/StringBuilder sb = new StringBuilder\(\);/, 'let sb = "";')
        .replace(/sb\.Append\('"'\);/g, 'sb += \'"\';')
        .replace(/string search =/g, 'let search =')
        .replace(/string searchNull =/g, 'let searchNull =')
        .replace(/string value =/g, 'let value =')
        .replace(/string hex =/g, 'let hex =')
        .replace(/int start =/g, 'let start =')
        .replace(/int end =/g, 'let end =')
        .replace(/int i =/g, 'let i =')
        .replace(/bool escaped =/g, 'let escaped =')
        .replace(/char c =/g, 'let c =')
        .replace(/char next =/g, 'let next =')
        .replace(/int code = Convert\.ToInt32\(hex, 16\);/, 'let code = parseInt(hex, 16);')
        .replace(/String\.IsNullOrEmpty\(s\)/g, '(!s)')
        .replace(/string\.IsNullOrEmpty\(s\)/g, '(!s)')
        .replace(/search\.Length/g, 'search.length')
        .replace(/json\.IndexOf/g, 'json.indexOf')
        .replace(/json\.Substring/g, 'json.substring')
        .replace(/s\.Substring/g, 's.substring')
        .replace(/json\.substring\(start, end - start\)/g, 'json.substring(start, end)')
        .replace(/s\.substring\(i \+ 2, 4\)/g, 's.substring(i + 2, i + 6)')
        .replace(/s\.Length/g, 's.length')
        .replace(/json\.Length/g, 'json.length')
        .replace(/sb\.Append\(\(char\)code\);/, 'sb += String.fromCharCode(code);')
        .replace(/sb\.Append\('([^']*)'\);/g, 'sb += "$1";')
        .replace(/sb\.Append\(s\[i\]\);/g, 'sb += s[i];')
        .replace(/return sb\.ToString\(\);/g, 'return sb;');

    return js;
}

test('Monitor GetJsonString handles escaped quotes inside JSON strings', () => {
    const js = [
        portMethodToJavaScript(extractMonitorMethod('GetJsonString')),
        portMethodToJavaScript(extractMonitorMethod('DecodeJsonString')),
        'return GetJsonString(json, key);'
    ].join('\n');

    const run = new Function('json', 'key', js);
    const json = JSON.stringify({ task: 'please edit "foo"', userMessage: 'line with "quote"' });

    assert.equal(run(json, 'task'), 'please edit "foo"');
    assert.equal(run(json, 'userMessage'), 'line with "quote"');
});
