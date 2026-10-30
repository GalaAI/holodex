// 4GE sandbox static server. Zero dependencies, so the app container needs
// nothing but node. Serves one folder on 0.0.0.0:$PORT inside the sandbox
// network; the host reaches it only through 127.0.0.1:17410.
//
// Route rules, so the route check sees real 404s instead of a masked SPA:
//   /x        -> x, x.html, x/index.html
//   missing   -> 404, unless the folder is a single-page app (exactly one
//                .html file), where index.html answers paths without a dot.
import { createReadStream, existsSync, readdirSync, statSync } from "node:fs";
import { createServer } from "node:http";
import { extname, join, normalize, resolve, sep } from "node:path";

const TYPES = {
  ".html": "text/html; charset=utf-8",
  ".htm": "text/html; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".mjs": "text/javascript; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".map": "application/json; charset=utf-8",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".jpg": "image/jpeg",
  ".jpeg": "image/jpeg",
  ".gif": "image/gif",
  ".webp": "image/webp",
  ".avif": "image/avif",
  ".ico": "image/x-icon",
  ".woff": "font/woff",
  ".woff2": "font/woff2",
  ".ttf": "font/ttf",
  ".txt": "text/plain; charset=utf-8",
  ".xml": "application/xml; charset=utf-8",
  ".webmanifest": "application/manifest+json",
  ".mp4": "video/mp4",
  ".webm": "video/webm",
  ".mp3": "audio/mpeg",
  ".wasm": "application/wasm",
};

function countHtml(dir, depth = 0) {
  if (depth > 4) return 0;
  let count = 0;
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.name === "node_modules" || entry.name.startsWith(".")) continue;
    if (entry.isDirectory()) count += countHtml(join(dir, entry.name), depth + 1);
    else if (entry.name.endsWith(".html")) count += 1;
    if (count > 1) return count;
  }
  return count;
}

export function startStaticServer(rootDir, port, host = "0.0.0.0") {
  const root = resolve(rootDir);
  const spa = countHtml(root) === 1;
  const isFile = (path) => existsSync(path) && statSync(path).isFile();

  function locate(urlPath) {
    let decoded;
    try {
      decoded = decodeURIComponent(urlPath);
    } catch {
      return null;
    }
    const target = normalize(join(root, decoded));
    if (target !== root && !target.startsWith(root + sep)) return null;
    for (const candidate of [target, `${target}.html`, join(target, "index.html")]) {
      if (isFile(candidate)) return candidate;
    }
    if (spa && !extname(decoded)) return join(root, "index.html");
    return null;
  }

  const server = createServer((request, response) => {
    const url = new URL(request.url ?? "/", "http://sandbox");
    if (request.method !== "GET" && request.method !== "HEAD") {
      response.writeHead(405, { allow: "GET, HEAD", "content-type": "text/plain; charset=utf-8" });
      response.end("This static app does not accept form posts. Wire a backend layer to handle them.\n");
      return;
    }
    const file = locate(url.pathname);
    if (!file) {
      response.writeHead(404, { "content-type": "text/html; charset=utf-8" });
      response.end(
        '<!doctype html><html lang="en"><head><title>Not found</title></head><body><h1>Not found</h1></body></html>\n',
      );
      return;
    }
    response.writeHead(200, {
      "content-type": TYPES[extname(file).toLowerCase()] ?? "application/octet-stream",
      "cache-control": "no-store",
    });
    if (request.method === "HEAD") {
      response.end();
      return;
    }
    createReadStream(file).pipe(response);
  });
  server.listen(port, host, () => {
    console.log(`sandbox static server: ${root} on ${host}:${port}${spa ? " (single-page app)" : ""}`);
  });
  return server;
}
