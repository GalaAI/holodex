import { app, BrowserWindow, ipcMain, shell } from 'electron';
import { promises as fs } from 'node:fs';
import { join } from 'node:path';

/**
 * Main process. Follows Electron's security checklist: context isolation on,
 * no Node in the renderer, sandboxed renderer, a strict CSP in index.html, and
 * external links open in the system browser. The renderer reaches the disk
 * only through the two IPC calls the preload script exposes.
 */
const notesFile = () => join(app.getPath('userData'), 'notes.json');

async function readNotes(): Promise<string[]> {
  try {
    const parsed: unknown = JSON.parse(await fs.readFile(notesFile(), 'utf8'));
    return Array.isArray(parsed) ? parsed.filter((note): note is string => typeof note === 'string') : [];
  } catch {
    return [];
  }
}

async function writeNotes(notes: unknown): Promise<boolean> {
  if (!Array.isArray(notes) || !notes.every((note) => typeof note === 'string' && note.length <= 2000)) return false;
  await fs.mkdir(app.getPath('userData'), { recursive: true });
  await fs.writeFile(notesFile(), JSON.stringify(notes.slice(0, 500), null, 2), 'utf8');
  return true;
}

function createWindow(): void {
  const window = new BrowserWindow({
    width: 1000,
    height: 720,
    backgroundColor: '#0b0d0e',
    show: false,
    webPreferences: {
      preload: join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });
  window.once('ready-to-show', () => window.show());
  window.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith('https://')) void shell.openExternal(url);
    return { action: 'deny' };
  });
  void window.loadFile(join(__dirname, '..', 'renderer', 'index.html'));
}

ipcMain.handle('notes:read', () => readNotes());
ipcMain.handle('notes:write', (_event, notes: unknown) => writeNotes(notes));
ipcMain.handle('app:info', () => ({ version: app.getVersion(), platform: process.platform }));

app.whenReady().then(() => {
  createWindow();
  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow();
  });
});

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit();
});
