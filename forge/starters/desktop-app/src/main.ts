import './style.css';
import brand from './brand.json';

/** What preload.ts exposes. In a plain browser (vite dev) it is missing, and the page says so. */
interface DesktopBridge {
  readNotes(): Promise<string[]>;
  writeNotes(notes: string[]): Promise<boolean>;
  info(): Promise<{ version: string; platform: string }>;
}

declare global {
  interface Window {
    desktop?: DesktopBridge;
  }
}

const root = document.getElementById('app');

type Props = Record<string, string | ((event: Event) => void)>;

function el<K extends keyof HTMLElementTagNameMap>(tag: K, props: Props = {}, ...children: Array<Node | string>): HTMLElementTagNameMap[K] {
  const node: HTMLElementTagNameMap[K] = document.createElement(tag);
  Object.assign(node, props);
  node.append(...children);
  return node;
}

async function start(): Promise<void> {
  if (!root) return;
  const bridge = window.desktop;
  const status = el('p', { className: 'status' });
  const list = el('ul', { className: 'list' });
  const input = el('input', { placeholder: 'Write a note and press Enter', ariaLabel: 'New note' });
  let notes: string[] = bridge ? await bridge.readNotes() : [];

  const draw = () => {
    list.replaceChildren(
      ...(notes.length === 0
        ? [el('li', { className: 'muted' }, 'No notes yet.')]
        : notes.map((note, index) =>
            el('li', {}, el('span', {}, note), el('button', { className: 'link', onclick: () => void remove(index) }, 'Remove')),
          )),
    );
  };
  const save = async () => {
    if (!bridge) {
      status.textContent = 'Running outside the desktop app, so notes are not saved.';
      return;
    }
    status.textContent = (await bridge.writeNotes(notes)) ? 'Saved.' : 'Could not save that note.';
  };
  const remove = async (index: number) => {
    notes = notes.filter((_, at) => at !== index);
    draw();
    await save();
  };

  input.addEventListener('keydown', async (event) => {
    if (event.key !== 'Enter') return;
    const text = input.value.trim();
    if (!text) return;
    notes = [text, ...notes];
    input.value = '';
    draw();
    await save();
  });

  const info = bridge ? await bridge.info() : undefined;
  root.append(
    el('p', { className: 'eyebrow' }, `// ${brand.name}`),
    el('h1', {}, brand.tagline),
    input,
    status,
    list,
    el('p', { className: 'foot' }, info ? `Version ${info.version} on ${info.platform}` : 'Preview in a browser'),
  );
  draw();
}

void start();
