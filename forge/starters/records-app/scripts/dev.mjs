// Development: the API (tsx watch, port 3000) and the Vite client (port 5173) together.
// Ctrl+C stops both.
import { spawn } from 'node:child_process';

const shell = process.platform === 'win32';
const children = [
  spawn('pnpm', ['exec', 'tsx', 'watch', 'server/index.ts'], { stdio: 'inherit', shell }),
  spawn('pnpm', ['exec', 'vite'], { stdio: 'inherit', shell }),
];

const stop = () => {
  for (const child of children) child.kill();
  process.exit(0);
};
process.on('SIGINT', stop);
process.on('SIGTERM', stop);
for (const child of children) child.on('exit', (code) => code && stop());
