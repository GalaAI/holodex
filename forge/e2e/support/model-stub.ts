import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";

/**
 * A scripted OpenAI-compatible endpoint for the e2e suite: GET /v1/models
 * and streaming POST /v1/chat/completions, the two calls the 4GE model
 * client makes through the openai SDK (packages/orchestra/src/model.ts).
 * The server under test is started with NEBIUS_BASE_URL pointing here and a
 * throwaway NEBIUS_API_KEY, so every edge case (an error, a hang, a slow or
 * failing layer) can be produced on demand. The orchestra code is the real
 * one; only the model's words are scripted.
 *
 * The answers follow the formats the orchestra asks for, adapted from the
 * ScriptedModel in packages/orchestra/src/flow.test.ts. The call's purpose is
 * read from the prompt (the system prompts of imagistation.ts, worker.ts,
 * design.ts, blueprint.ts and builds.ts), since purpose is not sent on the
 * wire. A layer keeps the starter's own files: the stub answers each file
 * deliverable with the <<<EXISTING>>> copy the worker showed it, so the app
 * still builds in the sandbox.
 */

export type StubMode = "ok" | "error" | "hang" | "refuse";

export interface StubCall {
  purpose: string;
  model: string;
  at: number;
  layer?: string;
}

export interface ModelStub {
  url: string;
  /** Base URL for NEBIUS_BASE_URL. */
  baseUrl: string;
  mode: StubMode;
  /** Layer title to the number of failing answers still to give. */
  layerFailures: Map<string, number>;
  /** Milliseconds to wait before answering a layer call (a slow sprint). */
  layerDelayMs: number;
  calls: StubCall[];
  /** Smith's actions for the next thread message, by layer key. */
  smithAction?: { layer: string; note: string };
  close(): Promise<void>;
}

export const STUB_MODELS = ["nvidia/nemotron-3-super-120b-a12b", "nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B", "nvidia/Nemotron-3.5-Super-VL"];

type Turn = { role: string; content: string };

/** What a call is for, read from its prompt. Most specific markers first. */
export function purposeOf(messages: Turn[]): string {
  const system = messages.filter((turn) => turn.role === "system").map((turn) => turn.content).join("\n");
  const firstUser = messages.find((turn) => turn.role === "user")?.content ?? "";
  if (system.includes("<<<FILE relative/path")) return "layer";
  if (system.includes("Write the checkpoint summary")) return "checkpoint.summary";
  if (system.includes("<<<MOCKUP Direction A")) return "design.mockups";
  if (system.includes("Draft the design system")) return "design.system";
  if (system.includes("Propose the AI orchestration")) return "design.orchestration";
  if (system.includes("You now write the Seed Scroll")) return "seed.draft";
  if (system.includes("Atlas crafting method")) return "blueprint.craft";
  if (firstUser.startsWith("Blueprint parameters:")) return "blueprint.parameters";
  if (system.includes("<<<STATE>>>")) return "imagistation.turn";
  return "session.turn";
}

/** Which scripted idea the conversation is about. */
type Idea = "tattoo" | "printer" | "nonsense" | "slow";

function ideaOf(text: string): Idea {
  const lower = text.toLowerCase();
  if (/label printer/.test(lower)) return "printer";
  if (/bakery/.test(lower)) return "slow";
  if (/tattoo|ink and iron/.test(lower)) return "tattoo";
  return "nonsense";
}

const SEEDS: Record<Idea, Record<string, unknown>> = {
  tattoo: {
    title: "Ink and Iron",
    problem: "Clients cannot see the portfolio in one place and booking requests get lost in direct messages",
    audience: "Adults in Portland looking for a custom tattoo, and the two artists and front desk who run the shop",
    successSignal: "Ten booking requests a week through the site",
    kind: "website",
    platforms: ["web"],
    features: ["Portfolio gallery by artist", "Services and pricing", "Hours and location", "Booking request form with the deposit explained"],
    constraints: ["Dark and bold look"],
    references: [],
  },
  slow: {
    title: "Rise Bakery",
    problem: "Customers cannot see the daily bake or order ahead",
    audience: "Neighbors who pick up bread on the way to work",
    successSignal: "Twenty preorders a week",
    kind: "website",
    platforms: ["web"],
    features: ["Daily bake list", "Preorder request form"],
    constraints: [],
    references: [],
  },
  printer: {
    title: "Label printer updater",
    problem: "Updating the shop's label printer firmware needs a vendor tool nobody can find",
    audience: "The shop owner who keeps the label printer running",
    successSignal: "The printer updates in one step",
    kind: "other",
    platforms: ["linux"],
    features: ["Find the printer on the network", "Flash the newest firmware"],
    constraints: [],
    references: [],
  },
  nonsense: {
    title: "Unclear idea",
    problem: "",
    audience: "",
    successSignal: "",
    kind: "other",
    platforms: ["web"],
    features: [],
    constraints: [],
    references: [],
  },
};

function imagistationTurn(messages: Turn[]): string {
  const userTurns = messages.filter((turn) => turn.role === "user");
  const all = userTurns.map((turn) => turn.content).join("\n");
  const idea = ideaOf(all);
  if (idea === "nonsense") {
    return `I could not tell what you want to build from that. What is it for, and who will use it?\n<<<STATE>>>\n${JSON.stringify({ draft: {}, questions: ["What is it for?", "Who will use it?"], ready: false })}`;
  }
  const seed = SEEDS[idea];
  const question =
    userTurns.length <= 1
      ? `Thanks. Who are the people who will use ${String(seed.title)}, and what does success look like?`
      : "That is clear. I can write the Seed Scroll now.";
  return `${question}\n<<<STATE>>>\n${JSON.stringify({
    draft: { title: seed.title, kind: seed.kind, platforms: seed.platforms, problem: seed.problem, audience: seed.audience },
    questions: userTurns.length <= 1 ? ["Who uses it?", "What does success look like?"] : [],
    ready: userTurns.length > 1,
  })}`;
}

function minimalFile(path: string, title: string): string {
  if (path.endsWith(".json")) return JSON.stringify({ title }, null, 2);
  if (/\.(c|m)?js$/.test(path)) return `export const layer = ${JSON.stringify(title)};\n`;
  if (/\.tsx?$/.test(path)) return `export const layer = ${JSON.stringify(title)};\n`;
  if (/\.html?$/.test(path)) return `<!doctype html><html><body><main>${title}</main></body></html>\n`;
  if (path.endsWith(".css")) return `/* ${title} */\n`;
  if (path.endsWith(".xml")) return `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>/</loc></url></urlset>\n`;
  return `${title}\n`;
}

function slug(text: string): string {
  return text.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "") || "layer";
}

/** A layer answer: the starter's files back as they are, every check reported. */
function layerAnswer(prompt: string, failing: boolean): { text: string; layer: string } {
  const layer = /Your layer: (.+?) \[/.exec(prompt)?.[1] ?? "Layer";
  const deliverables = (/^Deliverables: (.*)$/m.exec(prompt)?.[1] ?? "")
    .split("; ")
    .map((item) => item.trim())
    .filter((item) => /\.[A-Za-z0-9]{1,8}$/.test(item) && !/\s/.test(item));
  const checks = [...(/Checks your files must pass:\n([\s\S]*?)(?:\n\n|\n[A-Z]|$)/.exec(prompt)?.[1] ?? "").matchAll(/^\d+\. (.*)$/gm)].map(
    (match) => match[1] as string,
  );
  const existing = new Map<string, string>();
  for (const match of prompt.matchAll(/<<<EXISTING (.+?)>>>\n([\s\S]*?)\n<<<END EXISTING>>>/g)) {
    existing.set(match[1] as string, match[2] as string);
  }
  const files =
    deliverables.length > 0
      ? deliverables.map((path) => {
          const kept = existing.get(path);
          // The worker clips long files with "..."; a clipped copy would break the build, so write a small file instead.
          return { path, content: kept && !kept.endsWith("...") ? kept : minimalFile(path, layer) };
        })
      : [{ path: /Test flight/i.test(layer) ? `tests/${slug(layer)}.md` : `docs/layers/${slug(layer)}.md`, content: `# ${layer}\n\nWritten by the e2e model stub.\n` }];
  const text = [
    "<<<SUMMARY>>>",
    failing ? `${layer}: one check is not met yet.` : `${layer} is done and builds with the starter's setup.`,
    "<<<END SUMMARY>>>",
    ...files.flatMap((file) => [`<<<FILE ${file.path}>>>`, file.content, "<<<END FILE>>>"]),
    "<<<CHECKS>>>",
    JSON.stringify(
      checks.map((name, index) => ({
        name,
        passed: !(failing && index === 0),
        detail: failing && index === 0 ? "The stub was told to fail this check" : `${files[0]?.path ?? "the files"} meets it`,
      })),
    ),
    "<<<END CHECKS>>>",
  ].join("\n");
  return { text, layer };
}

function answer(stub: ModelStub, messages: Turn[]): { text: string; purpose: string; layer?: string } {
  const purpose = purposeOf(messages);
  const prompt = messages.map((turn) => turn.content).join("\n");
  // Agent system prompts carry examples (a tattoo shop among them); only the person's side names the idea.
  const said = messages.filter((turn) => turn.role === "user").map((turn) => turn.content).join("\n");
  switch (purpose) {
    case "imagistation.turn":
      return { purpose, text: imagistationTurn(messages) };
    case "seed.draft":
      return { purpose, text: JSON.stringify(SEEDS[ideaOf(said)]) };
    case "blueprint.parameters": {
      const keys = [...(/Answer with \{(.*)\}\.\s*$/.exec(prompt)?.[1] ?? "").matchAll(/"([^"]+)": "\.\.\."/g)].map((match) => match[1] as string);
      const values: Record<string, string> = {
        appName: "Ink and Iron",
        businessName: "Ink and Iron",
        contactEmail: "bookings@example.com",
        city: "Portland",
      };
      return { purpose, text: JSON.stringify(Object.fromEntries(keys.map((key) => [key, values[key] ?? ""]))) };
    }
    case "blueprint.craft":
      return {
        purpose,
        text: JSON.stringify({
          name: "Label printer updater",
          kind: "desktop-app",
          platforms: ["linux"],
          purpose: "A small tool that finds the shop's label printer and flashes the newest firmware",
          records: ["printers", "firmware versions"],
          actions: ["find the printer", "flash the firmware"],
          roles: ["staff"],
        }),
      };
    case "layer": {
      const layer = /Your layer: (.+?) \[/.exec(prompt)?.[1] ?? "Layer";
      const left = stub.layerFailures.get(layer) ?? 0;
      if (left > 0) stub.layerFailures.set(layer, left - 1);
      const result = layerAnswer(prompt, left > 0);
      return { purpose, text: result.text, layer };
    }
    case "checkpoint.summary":
      return { purpose, text: "The sprint built what the Blueprint planned. Every layer report is listed below with its checks." };
    case "design.system":
      return {
        purpose,
        text: JSON.stringify({
          name: "Ink",
          principles: ["Bold and clear", "The work comes first"],
          tokens: {
            colors: [
              { name: "ink", value: "#111111", role: "background" },
              { name: "bone", value: "#f2ede4", role: "text" },
              { name: "brass", value: "#c9a227", role: "primary" },
            ],
            typography: [{ name: "body", family: "Inter", size: "16px", weight: 400, lineHeight: "1.5" }],
            spacing: [{ name: "md", value: "16px" }],
            radii: [{ name: "sm", value: "4px" }],
            shadows: [],
            motion: [{ name: "quick", durationMs: 150, easing: "ease-out" }],
          },
          components: [{ name: "Gallery", category: "media", description: "Portfolio grid by artist", source: "21st", usedIn: ["/"] }],
        }),
      };
    case "design.mockups":
      return {
        purpose,
        text:
          "<<<MOCKUP Direction A: Gallery first>>>\n<!doctype html><html><head><title>A</title></head><body style=\"background:#111;color:#f2ede4;font-family:sans-serif\"><main><h1>Ink and Iron</h1><p>Custom blackwork and fine line in Portland.</p></main></body></html>\n<<<END MOCKUP>>>\n" +
          "<<<MOCKUP Direction B: Booking first>>>\n<!doctype html><html><head><title>B</title></head><body style=\"background:#f2ede4;color:#111;font-family:sans-serif\"><main><h1>Book your piece</h1><p>Tell us your idea and pick an artist.</p></main></body></html>\n<<<END MOCKUP>>>\n" +
          "<<<NOTES>>>A shows the work first. B drives booking requests.<<<END NOTES>>>",
      };
    case "design.orchestration":
      return {
        purpose,
        text: JSON.stringify({
          proposals: [{ title: "Weekly new work post", what: "Post the newest gallery pieces each week.", connects: ["app:ryozan"], cadence: "weekly" }],
        }),
      };
    default: {
      const last = [...messages].reverse().find((turn) => turn.role === "user")?.content ?? "";
      const thread = /^\[thread [^\]]+\]/.test(last);
      if (thread && stub.smithAction) {
        const action = stub.smithAction;
        stub.smithAction = undefined;
        return {
          purpose,
          text: `I will rebuild that layer with your note and report back here.\n<<<ACTIONS>>>\n${JSON.stringify([{ type: "rebuild-layer", layer: action.layer, note: action.note }])}`,
        };
      }
      return { purpose, text: thread ? "I read the thread. The frame matches the plan; tell me what to change and I will do it." : "Here is where the build stands." };
    }
  }
}

async function readBody(request: IncomingMessage): Promise<string> {
  const chunks: Buffer[] = [];
  for await (const chunk of request) chunks.push(chunk as Buffer);
  return Buffer.concat(chunks).toString("utf8");
}

function json(response: ServerResponse, status: number, body: unknown): void {
  response.writeHead(status, { "content-type": "application/json" });
  response.end(JSON.stringify(body));
}

export async function startModelStub(): Promise<ModelStub> {
  const sockets = new Set<import("node:net").Socket>();
  const stub = {
    mode: "ok" as StubMode,
    layerFailures: new Map<string, number>(),
    layerDelayMs: 0,
    calls: [] as StubCall[],
  } as ModelStub;

  const server: Server = createServer(async (request, response) => {
    const path = (request.url ?? "/").split("?")[0] ?? "/";
    if (stub.mode === "refuse") return json(response, 401, { error: { message: "Invalid API key", type: "invalid_request_error" } });
    if (request.method === "GET" && path === "/v1/models") {
      return json(response, 200, { object: "list", data: STUB_MODELS.map((id) => ({ id, object: "model", created: 0, owned_by: "e2e-stub" })) });
    }
    if (request.method === "POST" && path === "/v1/chat/completions") {
      const body = JSON.parse(await readBody(request)) as { model: string; messages: Turn[] };
      const reply = answer(stub, body.messages);
      stub.calls.push({ purpose: reply.purpose, model: body.model, at: Date.now(), ...(reply.layer ? { layer: reply.layer } : {}) });
      if (stub.mode === "error") return json(response, 500, { error: { message: "The e2e stub was told to fail", type: "server_error" } });
      if (stub.mode === "hang") return; // never answers; the client's timeout decides
      if (reply.purpose === "layer" && stub.layerDelayMs > 0) {
        await new Promise((resolve) => setTimeout(resolve, stub.layerDelayMs));
        if (response.destroyed || request.destroyed) return;
      }
      response.writeHead(200, { "content-type": "text/event-stream", "cache-control": "no-cache", connection: "keep-alive" });
      const id = `chatcmpl-e2e-${stub.calls.length}`;
      const created = Math.floor(Date.now() / 1000);
      for (const piece of reply.text.match(/[\s\S]{1,200}/g) ?? [""]) {
        response.write(`data: ${JSON.stringify({ id, object: "chat.completion.chunk", created, model: body.model, choices: [{ index: 0, delta: { content: piece }, finish_reason: null }] })}\n\n`);
      }
      response.write(`data: ${JSON.stringify({ id, object: "chat.completion.chunk", created, model: body.model, choices: [{ index: 0, delta: {}, finish_reason: "stop" }] })}\n\n`);
      response.write(`data: ${JSON.stringify({ id, object: "chat.completion.chunk", created, model: body.model, choices: [], usage: { prompt_tokens: 100, completion_tokens: 50, total_tokens: 150 } })}\n\n`);
      response.end("data: [DONE]\n\n");
      return;
    }
    json(response, 404, { error: { message: `No stub route for ${request.method} ${path}` } });
  });
  server.on("connection", (socket) => {
    sockets.add(socket);
    socket.on("close", () => sockets.delete(socket));
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address() as AddressInfo;
  stub.url = `http://127.0.0.1:${port}`;
  stub.baseUrl = `${stub.url}/v1`;
  stub.close = () =>
    new Promise<void>((resolve) => {
      for (const socket of sockets) socket.destroy();
      server.close(() => resolve());
    });
  return stub;
}
