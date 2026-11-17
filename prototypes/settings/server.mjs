import { watch } from 'node:fs';
import { readFile } from 'node:fs/promises';
import { createServer } from 'node:http';

const host = '127.0.0.1';
const root = new URL('./', import.meta.url);
const files = new Map([
  ['/', ['index.html', 'text/html; charset=utf-8']],
  ['/index.html', ['index.html', 'text/html; charset=utf-8']],
  ['/styles.css', ['styles.css', 'text/css; charset=utf-8']],
  ['/app.js', ['app.js', 'text/javascript; charset=utf-8']],
  ['/favicon.svg', ['favicon.svg', 'image/svg+xml']],
  ...['deepseek-color','openai','gemini-color','tencentcloud-color'].map(name =>
    [`/assets/${name}.svg`, [`assets/${name}.svg`, 'image/svg+xml']]),
]);
const watchedFiles = new Set(['index.html', 'styles.css', 'app.js']);

function start() {
  const portText = process.env.STOOL_SETTINGS_PORT ?? '5173';
  const port = Number(portText);
  if (!/^\d+$/.test(portText) || !Number.isInteger(port) || port < 1 || port > 65535) {
    throw new Error('STOOL_SETTINGS_PORT must be an integer between 1 and 65535.');
  }

  const clients = new Set();
  const pendingChanges = new Map();
  let version = Date.now();
  let stopping = false;
  let watcher;

  function event(name, data) {
    return `event: ${name}\ndata: ${JSON.stringify(data)}\n\n`;
  }

  function shutdown(exitCode = 0) {
    if (stopping) return;
    stopping = true;
    process.exitCode = exitCode;
    watcher?.close();
    clearInterval(heartbeat);
    for (const timer of pendingChanges.values()) clearTimeout(timer);
    pendingChanges.clear();
    for (const client of clients) client.end();
    clients.clear();
    server.close();
    server.closeAllConnections();
  }

  const server = createServer(async (request, response) => {
    // Match literal paths against fixed filenames; never resolve a request into a disk path.
    const pathname = (request.url ?? '/').split('?')[0];
    response.setHeader('Cache-Control', 'no-store');
    response.setHeader('X-Content-Type-Options', 'nosniff');

    if (request.method !== 'GET' && request.method !== 'HEAD') {
      response.writeHead(405, { Allow: pathname === '/__events' ? 'GET' : 'GET, HEAD' });
      response.end('Method not allowed');
      return;
    }

    if (pathname === '/__events') {
      if (request.method !== 'GET') {
        response.writeHead(405, { Allow: 'GET' });
        response.end();
        return;
      }
      response.writeHead(200, {
        'Content-Type': 'text/event-stream; charset=utf-8',
        Connection: 'keep-alive',
        'X-Accel-Buffering': 'no',
      });
      response.write(`retry: 1000\n${event('ready', { version })}`);
      clients.add(response);
      response.on('close', () => clients.delete(response));
      response.on('error', () => {
        clients.delete(response);
        response.destroy();
      });
      return;
    }

    const file = files.get(pathname);
    if (!file) {
      response.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
      response.end(request.method === 'HEAD' ? undefined : 'Not found');
      return;
    }

    try {
      const [filename, contentType] = file;
      const content = await readFile(new URL(filename, root));
      response.writeHead(200, {
        'Content-Type': contentType,
        'Content-Length': content.length,
      });
      response.end(request.method === 'HEAD' ? undefined : content);
    } catch (error) {
      const missing = error.code === 'ENOENT' || error.code === 'ENOTDIR';
      if (!missing) console.error(`Could not read ${file[0]}: ${error.message}`);
      response.writeHead(missing ? 404 : 500, { 'Content-Type': 'text/plain; charset=utf-8' });
      response.end(request.method === 'HEAD' ? undefined : missing ? 'Not found' : 'Could not read file');
    }
  });

  const heartbeat = setInterval(() => {
    for (const client of clients) {
      if (!client.write(': heartbeat\n\n')) client.destroy();
    }
  }, 25_000);

  server.on('error', (error) => {
    const message = error.code === 'EADDRINUSE'
      ? `Port ${port} is already in use. Stop the existing server or set STOOL_SETTINGS_PORT to another port.`
      : error.message;
    console.error(`Settings preview could not start at http://${host}:${port}/: ${message}`);
    shutdown(1);
  });

  try {
    // Watching the directory preserves refresh across atomic file replacements.
    // Only these three direct children can publish events; no recursive watching.
    watcher = watch(root, { encoding: 'utf8' }, (_eventType, filename) => {
      if (stopping || !watchedFiles.has(filename)) return;
      clearTimeout(pendingChanges.get(filename));
      pendingChanges.set(filename, setTimeout(() => {
        pendingChanges.delete(filename);
        version = Math.max(Date.now(), version + 1);
        const message = event('change', { filename, version });
        for (const client of clients) {
          if (!client.write(message)) client.destroy();
        }
      }, 80));
    });
    watcher.on('error', (error) => {
      console.error(`Settings preview file watcher failed: ${error.message}`);
      shutdown(1);
    });
    process.once('SIGINT', () => shutdown());
    process.once('SIGTERM', () => shutdown());
    server.listen(port, host, () => {
      console.log(`STool settings preview: http://${host}:${port}/`);
      console.log('Live refresh watches index.html, styles.css and app.js. Press Ctrl+C to stop.');
    });
  } catch (error) {
    shutdown(1);
    throw error;
  }
}

try {
  start();
} catch (error) {
  console.error(`Settings preview could not start: ${error.message}`);
  process.exitCode = 1;
}
