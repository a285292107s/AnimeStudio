// Reloads the host page and records uncaught exceptions / console errors.
// 用法：node tools/cdp-errors.mjs [持续毫秒]
import fs from 'node:fs';

const endpoint = process.env.CDP_ENDPOINT || 'http://127.0.0.1:9222';
const holdMs = Number(process.argv[2] || 6000);

const targets = await (await fetch(`${endpoint}/json`)).json();
const page = targets.find((target) => target.type === 'page');
if (!page) {
  console.error('no page target on CDP endpoint');
  process.exit(1);
}

const socket = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((resolve, reject) => {
  socket.onopen = resolve;
  socket.onerror = reject;
});

let seq = 0;
const pending = new Map();
const events = [];

socket.onmessage = (event) => {
  const message = JSON.parse(event.data);
  if (message.id && pending.has(message.id)) {
    pending.get(message.id)(message);
    pending.delete(message.id);
    return;
  }
  if (message.method === 'Runtime.exceptionThrown') {
    const details = message.params.exceptionDetails;
    events.push({
      kind: 'exception',
      text: details.exception?.description || details.text,
      url: details.url,
      line: details.lineNumber,
      stack: details.stackTrace?.callFrames?.slice(0, 6).map((frame) => `${frame.functionName}@${frame.url}:${frame.lineNumber}`),
    });
  }
  if (message.method === 'Runtime.consoleAPICalled' && ['error', 'warning'].includes(message.params.type)) {
    events.push({
      kind: `console.${message.params.type}`,
      text: message.params.args?.map((arg) => arg.value ?? arg.description).join(' '),
    });
  }
  if (message.method === 'Log.entryAdded' && message.params.entry.level === 'error') {
    events.push({ kind: 'log', text: message.params.entry.text, url: message.params.entry.url });
  }
};

const send = (method, params = {}) =>
  new Promise((resolve) => {
    const id = ++seq;
    pending.set(id, resolve);
    socket.send(JSON.stringify({ id, method, params }));
  });

await send('Runtime.enable');
await send('Log.enable');
await send('Page.enable');
await send('Page.reload', { ignoreCache: true });

await new Promise((resolve) => setTimeout(resolve, holdMs));

socket.close();
console.log(JSON.stringify(events, null, 2));
