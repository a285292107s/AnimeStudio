// 通过 WebView2 的 CDP 端口检查宿主页面（Node 22+ 自带 WebSocket）
// 用法：
//   node tools/cdp-probe.mjs                 -> 打印页面关键状态 + 截图
//   node tools/cdp-probe.mjs "表达式" [out]  -> 执行表达式并截图

import fs from 'node:fs';
import path from 'node:path';

const endpoint = process.env.CDP_ENDPOINT || 'http://127.0.0.1:9222';
// 表达式可以直接给，也可以用 @文件 从磁盘读取（避免 PowerShell 转义地狱）
const rawExpression = process.argv[2];
const expression = rawExpression?.startsWith('@')
  ? fs.readFileSync(rawExpression.slice(1), 'utf8')
  : rawExpression ||
  `({
     title: document.title,
     mode: document.querySelector('#modeChip')?.textContent,
     chip: document.querySelector('#indexChipText')?.textContent,
     indexMeta: document.querySelector('#indexMeta')?.textContent,
     textCount: document.querySelector('#textCount')?.textContent,
     imageCount: document.querySelector('#imageCount')?.textContent,
     status: document.querySelector('#statusText')?.textContent,
     logLines: [...document.querySelectorAll('#logList li')].slice(-6).map(li => li.textContent),
     stats: document.querySelector('#stats')?.innerText?.replace(/\\n/g, ' | ')
   })`;

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
socket.onmessage = (event) => {
  const message = JSON.parse(event.data);
  if (message.id && pending.has(message.id)) {
    pending.get(message.id)(message);
    pending.delete(message.id);
  }
};

const send = (method, params = {}) => new Promise((resolve) => {
  const id = ++seq;
  pending.set(id, resolve);
  socket.send(JSON.stringify({ id, method, params }));
});

await send('Runtime.enable');
await send('Page.enable');

const evaluated = await send('Runtime.evaluate', {
  expression,
  returnByValue: true,
  awaitPromise: true,
});

if (evaluated.result?.exceptionDetails) {
  console.log('EXCEPTION:', JSON.stringify(evaluated.result.exceptionDetails, null, 2));
} else {
  console.log(JSON.stringify(evaluated.result?.result?.value, null, 2));
}

const outPath = process.argv[3] || path.join(process.env.TEMP || '.', 'host-shot.png');
const shot = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
if (shot.result?.data) {
  fs.writeFileSync(outPath, Buffer.from(shot.result.data, 'base64'));
  console.log(`\nscreenshot -> ${outPath}`);
}

socket.close();
