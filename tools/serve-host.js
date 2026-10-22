// Tiny static server for the Host UI prototype.
// The real app serves the same folder through WebView2's virtual host mapping;
// this script exists so the UI can be designed and reviewed in a normal browser.
//
//   node tools/serve-host.js            -> http://127.0.0.1:8900
//   node tools/serve-host.js --port 9000

const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '..', 'AnimeStudio.Host', 'wwwroot');
const portArgIndex = process.argv.indexOf('--port');
const port = portArgIndex > 0 ? Number(process.argv[portArgIndex + 1]) : 8900;

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.webp': 'image/webp',
  '.svg': 'image/svg+xml',
  '.woff2': 'font/woff2',
  '.ico': 'image/x-icon',
};

const server = http.createServer((req, res) => {
  let urlPath = decodeURIComponent((req.url || '/').split('?')[0]);

  if (urlPath === '/') {
    urlPath = '/index.html';
  }

  const filePath = path.join(root, path.normalize(urlPath).replace(/^([/\\])+/, ''));

  if (!filePath.startsWith(root)) {
    res.writeHead(403).end('forbidden');
    return;
  }

  fs.readFile(filePath, (err, data) => {
    if (err) {
      res.writeHead(404, { 'content-type': 'text/plain; charset=utf-8' });
      res.end(`not found: ${urlPath}`);
      return;
    }

    res.writeHead(200, {
      'content-type': MIME[path.extname(filePath).toLowerCase()] || 'application/octet-stream',
      'cache-control': 'no-store',
    });
    res.end(data);
  });
});

server.listen(port, '127.0.0.1', () => {
  console.log(`Host UI  ->  http://127.0.0.1:${port}`);
  console.log(`serving  ->  ${root}`);
});
