#!/usr/bin/env node
// Holodex UI Lab bridge: a stdio MCP server for the PC Holodex runs on. Every tool is one line sent to Holodex's
// control pipe (ArkStation.Holodex.Control) and one line read back, so this server holds no state of its own and can
// do nothing the pipe does not already offer. It reads files in one folder only: the PNG and element map Holodex
// writes under %TEMP%\Holodex\ui-lab when asked to render a surface.
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { CallToolRequestSchema, ListToolsRequestSchema } from "@modelcontextprotocol/sdk/types.js";
import { mkdir, readFile } from "node:fs/promises";
import net from "node:net";
import os from "node:os";
import path from "node:path";

const PIPE = "\\\\.\\pipe\\ArkStation.Holodex.Control";

// Holodex resolves the same folder from Path.GetTempPath(), which reads TMP before TEMP; a path outside it is refused.
const RENDER_ROOT = path.join(process.env.TMP || process.env.TEMP || os.tmpdir(), "Holodex", "ui-lab");

// Holodex reads at most 1024 bytes of a command, so a longer line would arrive cut short and half applied.
const MAX_COMMAND_BYTES = 1000;

const SURFACE_NAME = /^[a-z0-9-]+$/;

/** One line in, one line out. Holodex answers a render in a few seconds; a first render of every state can take longer. */
function sendPipe(command, timeoutMs = 120000) {
  return new Promise((resolve, reject) => {
    if (/[\r\n]/.test(command)) return reject(new Error("a pipe command is one line"));
    if (Buffer.byteLength(command, "utf8") > MAX_COMMAND_BYTES) return reject(new Error(`the command is over ${MAX_COMMAND_BYTES} bytes, more than Holodex reads; send less at once`));
    const socket = net.connect(PIPE);
    let reply = "";
    let settled = false;
    const finish = (error) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      socket.destroy();
      const line = reply.split(/\r?\n/)[0].trim();
      if (error) reject(error);
      else if (!line) reject(new Error("Holodex closed the pipe without answering"));
      else resolve(line);
    };
    const timer = setTimeout(() => finish(new Error(`Holodex did not answer within ${Math.round(timeoutMs / 1000)} seconds`)), timeoutMs);
    socket.setEncoding("utf8");
    socket.on("connect", () => socket.write(command + "\n"));
    socket.on("data", (chunk) => { reply += chunk; if (reply.includes("\n")) finish(); });
    socket.on("end", () => finish());
    socket.on("close", () => finish());
    socket.on("error", (error) => finish(error.code === "ENOENT"
      ? new Error("Holodex is not running, or its control pipe is not open. Start Holodex with HOLODEX_DEV_PIPE=1.")
      : error));
  });
}

// The pipe serves one client at a time and every command runs on the app's UI thread, so calls go in order.
let queue = Promise.resolve();
function ask(command, timeoutMs) {
  const next = queue.then(() => sendPipe(command, timeoutMs));
  queue = next.catch(() => {});
  return next;
}

/** The JSON after "ok:<prefix>:", or an Error carrying Holodex's own refusal. */
async function askJson(command, prefix, timeoutMs) {
  const line = await ask(command, timeoutMs);
  if (!line.startsWith(`ok:${prefix}:`)) throw new Error(line);
  const rest = line.slice(prefix.length + 4);
  // holodex:tune:set answers "refused a,b:{...}" when it skipped names it does not tune.
  const at = rest.search(/[\[{]/);
  if (at < 0) throw new Error(line);
  return { note: rest.slice(0, at).replace(/:$/, ""), value: JSON.parse(rest.slice(at)) };
}

const text = (value) => ({ type: "text", text: typeof value === "string" ? value : JSON.stringify(value) });

const READ = { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false };
const TUNE = { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false };

const tools = [
  {
    name: "list_surfaces",
    title: "List Holodex surfaces",
    description: "Names of every surface the running Holodex can render (hud, holodex-mods, wiz-agent, menu, toast-info and so on). Returns a JSON array of names.",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
    annotations: READ,
    async run() {
      const { value } = await askJson("ui:surfaces", "ui:surfaces");
      return { content: [text(value)] };
    },
  },
  {
    name: "render_surface",
    title: "Render one Holodex surface",
    description: "Draws one surface offscreen in the running Holodex, in the same state the as-built render uses, and returns the PNG as an image plus its element map as JSON: {surface,width,height,elements:[{id,name,type,x,y,w,h,depth,parent,text,component,tokens}]}. Boxes are PNG pixels. A surface that is painted and not built from controls (toasts, tray composites, the cursor sheet) returns the image and a null map.",
    inputSchema: {
      type: "object",
      properties: { name: { type: "string", pattern: "^[a-z0-9-]+$", description: "A surface name from list_surfaces, for example holodex-mods." } },
      required: ["name"],
      additionalProperties: false,
    },
    annotations: READ,
    async run({ name }) {
      if (typeof name !== "string" || !SURFACE_NAME.test(name)) throw new Error("name is a surface name from list_surfaces: lowercase letters, digits and dashes");
      await mkdir(RENDER_ROOT, { recursive: true });
      const png = path.join(RENDER_ROOT, `${name}.png`);
      const { value } = await askJson(`ui:render:${name}:${png}`, "ui:render", 180000);
      // Read what was asked for, never a path taken from the reply.
      const image = await readFile(png);
      const map = value.map ? JSON.parse(await readFile(path.join(RENDER_ROOT, `${name}.json`), "utf8")) : null;
      return {
        content: [
          { type: "image", data: image.toString("base64"), mimeType: "image/png" },
          text({ surface: name, width: value.width, height: value.height, map }),
        ],
      };
    },
  },
  {
    name: "element",
    title: "Read one Holodex control",
    description: "Current properties of one control on a surface, by the id its element map gives it: type, component, bounds, text, backColor and foreColor as hex, font, padding, and the Phosphor tokens it paints with and their current values. Returns JSON.",
    inputSchema: {
      type: "object",
      properties: {
        surface: { type: "string", pattern: "^[a-z0-9-]+$", description: "A surface name from list_surfaces." },
        id: { type: "string", description: "The id of an element in that surface's element map (render_surface returns the map)." },
      },
      required: ["surface", "id"],
      additionalProperties: false,
    },
    annotations: READ,
    async run({ surface, id }) {
      if (typeof surface !== "string" || !SURFACE_NAME.test(surface)) throw new Error("surface is a surface name from list_surfaces");
      if (typeof id !== "string" || !id.trim()) throw new Error("id is an element id from the surface's element map");
      const { value } = await askJson(`ui:element:${surface}:${id.trim()}`, "ui:element", 180000);
      return { content: [text(value)] };
    },
  },
  {
    name: "tune_get",
    title: "Read the Holodex tuning overlay",
    description: "Every tunable Phosphor token with its effective value and whether the live overlay sets it. Returns JSON: {\"<token>\":{\"value\":\"240ms\",\"tuned\":false}}.",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
    annotations: READ,
    async run() {
      const { value } = await askJson("holodex:tune:get", "holodex:tune");
      return { content: [text(value)] };
    },
  },
  {
    name: "tune_set",
    title: "Tune Holodex tokens live",
    description: "Merges token values into Holodex's tuning overlay (tuning.json); the running app repaints with them at once. values is a flat object of token name to CSS value, for example {\"dur-disclosure\":\"320ms\"}; null removes one override. Only names tune_get lists are applied. Returns the overlay after the change, and the names it refused.",
    inputSchema: {
      type: "object",
      properties: {
        values: {
          type: "object",
          description: "Token name to value. Strings and numbers set an override, null clears it.",
          additionalProperties: { type: ["string", "number", "null"] },
        },
      },
      required: ["values"],
      additionalProperties: false,
    },
    annotations: TUNE,
    async run({ values }) {
      if (!values || typeof values !== "object" || Array.isArray(values)) throw new Error("values is an object of token name to value");
      for (const [name, value] of Object.entries(values))
        if (value !== null && typeof value !== "string" && typeof value !== "number") throw new Error(`${name}: a value is a string, a number or null`);
      const { note, value } = await askJson(`holodex:tune:set:${JSON.stringify(values)}`, "holodex:tune");
      return { content: [text({ refused: note ? note.replace(/^refused /, "").split(",") : [], tuning: value })] };
    },
  },
  {
    name: "tune_reset",
    title: "Clear the Holodex tuning overlay",
    description: "Removes every live override, so each token is back at its shipped value. Returns the overlay after the reset.",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
    annotations: { ...TUNE, destructiveHint: true },
    async run() {
      const { value } = await askJson("holodex:tune:reset", "holodex:tune");
      return { content: [text(value)] };
    },
  },
];

const server = new Server({ name: "holodex", version: "0.1.0" }, { capabilities: { tools: {} } });

server.setRequestHandler(ListToolsRequestSchema, async () => ({
  tools: tools.map(({ name, title, description, inputSchema, annotations }) => ({ name, title, description, inputSchema, annotations: { title, ...annotations } })),
}));

server.setRequestHandler(CallToolRequestSchema, async (request) => {
  const tool = tools.find((t) => t.name === request.params.name);
  if (!tool) return { isError: true, content: [text(`unknown tool ${request.params.name}`)] };
  try {
    return await tool.run(request.params.arguments ?? {});
  } catch (error) {
    return { isError: true, content: [text(error instanceof Error ? error.message : String(error))] };
  }
});

await server.connect(new StdioServerTransport());
