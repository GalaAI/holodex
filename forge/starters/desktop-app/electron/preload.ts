import { contextBridge, ipcRenderer } from 'electron';

/** The only bridge between the page and the main process. Keep it small and typed. */
const desktop = {
  readNotes: (): Promise<string[]> => ipcRenderer.invoke('notes:read'),
  writeNotes: (notes: string[]): Promise<boolean> => ipcRenderer.invoke('notes:write', notes),
  info: (): Promise<{ version: string; platform: string }> => ipcRenderer.invoke('app:info'),
};

contextBridge.exposeInMainWorld('desktop', desktop);

export type DesktopBridge = typeof desktop;
