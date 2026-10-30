import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import {
  BUILT_IN_MODS,
  LAUNCH_MOD_ADAPTERS,
  componentCategorySchema,
  componentInventoryItemSchema,
  designSystemSchema,
  designTokensSchema,
  type Blueprint,
  type Build,
  type ComponentInventoryItem,
  type DesignSystem,
  type Preview,
  type Seed,
} from "@4ge/contracts";
import type { DataPaths } from "@4ge/contracts/node";
import { z } from "zod";
import { AGENT_DEFINITIONS } from "./agents.js";
import { askJson } from "./ask.js";
import { isAbort, messageOf } from "./errors.js";
import type { ModelClient } from "./model.js";
import { block, fileBlocks } from "./parse.js";
import { LayerNotConnected, type DesignPort } from "./ports.js";
import type { SessionManager } from "./sessions.js";
import { clip, newId, now, readJson, slugify, writeJson, type EventSink } from "./util.js";

/**
 * Smith's design conversation (SPEC flow 5 and 6). While the orchestra
 * builds the hidden layers, Smith drafts the design system, the component
 * inventory and two landing page directions as artboards for the
 * StageCanvas, then proposes the AI orchestration that would manage and
 * promote the result. Smith proposes; the person picks in the thread.
 *
 * The design system and inventory go to the design layer's routes; when
 * that layer is not connected they are kept in builds/<id>/design/smith/
 * and Smith says so. Mockups are written to design/smith/mockups/*.html and
 * published as previews (refs relative to the build folder).
 */

export interface DesignDeps {
  paths: DataPaths;
  sessions: SessionManager;
  models: ModelClient;
  events: EventSink;
  design: DesignPort;
}

const designDraftSchema = z.object({
  name: z.string().min(1).max(160),
  principles: z.array(z.string().min(1).max(400)).max(20).default([]),
  tokens: designTokensSchema,
  components: z
    .array(
      z.object({
        name: z.string().min(1).max(120),
        category: componentCategorySchema.catch("other"),
        description: z.string().min(1).max(1200),
        source: z.enum(["kit", "21st", "generated", "custom"]).catch("generated"),
        sourceRef: z.string().max(400).optional(),
        usedIn: z.array(z.string().min(1).max(200)).max(80).default([]),
      }),
    )
    .max(40)
    .default([]),
});

const orchestrationSchema = z.object({
  proposals: z
    .array(
      z.object({
        title: z.string().min(1).max(160),
        what: z.string().min(1).max(800),
        connects: z.array(z.string().min(1).max(160)).max(12).default([]),
        cadence: z.string().max(160).optional(),
      }),
    )
    .min(1)
    .max(8),
});
export type OrchestrationProposal = z.infer<typeof orchestrationSchema>["proposals"][number];

const KNOWN_CONNECTIONS = [
  "app:ryozan",
  ...BUILT_IN_MODS,
  ...LAUNCH_MOD_ADAPTERS.map((id) => (id.startsWith("adapter:") ? id : `adapter:${id}`)),
  "adapter:nebius",
  "adapter:hivemind",
];

function brief(seed: Seed, blueprint: Blueprint): string {
  return JSON.stringify(
    {
      title: seed.title,
      idea: seed.idea,
      audience: seed.audience,
      problem: seed.problem,
      features: seed.features,
      constraints: seed.constraints,
      kind: blueprint.kind,
      platforms: blueprint.platforms,
      interfaceLayers: blueprint.stages
        .filter((stage) => stage.stage === "interface")
        .flatMap((stage) => stage.layers.map((layer) => layer.title)),
    },
    null,
    1,
  );
}

export async function runDesignConversation(deps: DesignDeps, build: Build, seed: Seed, blueprint: Blueprint): Promise<void> {
  const root = build.rootSessionId as string;
  const session = deps.sessions.create({
    agent: "smith",
    kind: "chat",
    parentId: root,
    task: "Design system, component inventory and landing page directions",
    buildId: build.id,
    subject: { type: "build", id: build.id, label: "Design" },
    status: "running",
    summary: "Drafting the design system",
  });
  const signal = deps.sessions.signal(session.id);
  const folder = join(deps.paths.build(build.id).design, "smith");
  mkdirSync(join(folder, "mockups"), { recursive: true });
  try {
    // 1. Design system and inventory.
    const { value: draft, result } = await askJson(deps.models, {
      tier: "lead",
      purpose: "design.system",
      system: `${AGENT_DEFINITIONS.smith.system}

Draft the design system and component inventory for this build. Colors are data the person will tune: give hex values with roles (background, surface, text, muted, primary, accent, success, warning, danger, border). Two or three type styles with real font families, a spacing scale, radii and two motion tokens. List the components the pages need, preferring well known registry components (source "21st") or generated ones. Answer with one JSON object and nothing else:
{"name": string, "principles": [plain sentences], "tokens": {"colors": [{"name","value","role"}], "typography": [{"name","family","size","weight","lineHeight"}], "spacing": [{"name","value"}], "radii": [{"name","value"}], "shadows": [{"name","value"}], "motion": [{"name","durationMs","easing"}]}, "components": [{"name","category" (layout, navigation, input, display, feedback, media, commerce, booking, content, other),"description","source" (kit, 21st, generated, custom),"usedIn": [page paths]}]}`,
      messages: [{ role: "user", content: brief(seed, blueprint) }],
      schema: designDraftSchema,
      maxTokens: 5000,
      signal,
    });
    deps.sessions.update(session.id, { model: result.model, summary: "Laying out landing page directions" });
    const system: DesignSystem = designSystemSchema.parse({
      id: newId("designSystem"),
      buildId: build.id,
      name: draft.name,
      source: "generated",
      tokens: draft.tokens,
      principles: draft.principles,
      updatedAt: now(),
    });
    const items: ComponentInventoryItem[] = draft.components.map((component) =>
      componentInventoryItemSchema.parse({
        id: newId("component"),
        buildId: build.id,
        name: component.name,
        category: component.category,
        status: "proposed",
        description: component.description,
        source: component.source,
        ...(component.sourceRef ? { sourceRef: component.sourceRef } : {}),
        usedIn: component.usedIn,
        updatedAt: now(),
      }),
    );
    writeJson(join(folder, "design-system.json"), system);
    writeJson(join(folder, "inventory.json"), items);
    // The design layer owns the canonical copy; the files above are Smith's working copy.
    let designNote = "";
    try {
      await deps.design.saveSystem(system);
      for (const item of items) await deps.design.saveInventoryItem(item);
    } catch (error) {
      if (isAbort(error, signal)) throw error;
      designNote =
        error instanceof LayerNotConnected
          ? "The design layer is not connected yet, so the system and inventory are kept in the build's design folder."
          : `The design layer did not take the system (${messageOf(error)}), so it is kept in the build's design folder.`;
      deps.sessions.log(session.id, "warn", designNote);
    }

    // 2. Two landing page directions, as artboards.
    const mockupAnswer = await deps.models.chat({
      tier: "lead",
      purpose: "design.mockups",
      messages: [
        {
          role: "system",
          content: `${AGENT_DEFINITIONS.smith.system}

Lay out two distinct landing page directions for this build as complete, self-contained HTML documents (inline CSS, no external scripts, system or Google fonts only, real copy drawn from the Seed, no lorem ipsum, images as CSS shapes or gradients). Use the design tokens given. Each page should be responsive and under 400 lines.
Answer in exactly this format:
<<<MOCKUP Direction A: short name>>>
<!doctype html>...
<<<END MOCKUP>>>
<<<MOCKUP Direction B: short name>>>
<!doctype html>...
<<<END MOCKUP>>>
<<<NOTES>>>
One sentence per direction: what it emphasizes and who it suits.
<<<END NOTES>>>`,
        },
        { role: "user", content: `${brief(seed, blueprint)}\n\nDesign system:\n${JSON.stringify({ name: system.name, tokens: system.tokens }, null, 1)}` },
      ],
      maxTokens: 14_000,
      temperature: 0.6,
      signal,
    });
    const mockups = fileBlocks(mockupAnswer.text, "MOCKUP").slice(0, 3);
    const previews: Preview[] = [];
    for (const mockup of mockups) {
      const slug = slugify(mockup.path, "direction");
      const ref = `design/smith/mockups/${slug}.html`;
      writeFileSync(join(folder, "mockups", `${slug}.html`), mockup.content, "utf8");
      const preview: Preview = { kind: "html", title: clip(mockup.path, 160), ref, stage: "interface", updatedAt: now() };
      previews.push(preview);
      deps.events.publish({ type: "preview.updated", buildId: build.id, sessionId: session.id, data: { buildId: build.id, preview } });
    }
    const notes = block(mockupAnswer.text, "NOTES") ?? "";

    const palette = system.tokens.colors
      .filter((color) => ["primary", "accent", "background"].includes(color.role))
      .map((color) => `${color.role} ${color.value}`)
      .join(", ");
    deps.sessions.addMessage(root, {
      role: "assistant",
      agent: "smith",
      content: [
        `While the orchestra builds, here is a first design system: ${system.name}${palette ? ` (${palette})` : ""}, with ${system.tokens.typography.length} type styles and ${items.length} components proposed for the pages.`,
        previews.length > 0
          ? `${previews.length} landing page direction${previews.length === 1 ? " is" : "s are"} on the canvas: ${previews.map((preview) => preview.title).join("; ")}. ${notes}`.trim()
          : "The landing page directions did not come back from the model; I will lay them out again when you ask.",
        designNote,
        "Which direction should I take forward, and what would you change?",
      ]
        .filter(Boolean)
        .join("\n\n"),
      usage: mockupAnswer.usage,
    });

    // 3. The post-launch orchestration (SPEC flow 6).
    deps.sessions.update(session.id, { summary: "Proposing how to run it after launch" });
    const { value: plan } = await askJson(deps.models, {
      tier: "lead",
      purpose: "design.orchestration",
      system: `${AGENT_DEFINITIONS.smith.system}

Propose the AI orchestration that would manage and promote this build after launch (for a site: content, SEO, social, bookings and similar). Connect each proposal to Ryozan (app:ryozan, the person's AI operations app), mods (${BUILT_IN_MODS.join(", ")}), adapters (${LAUNCH_MOD_ADAPTERS.join(", ")}) or MCP server augments (augment:<name>) as needed. Only propose what this build needs. Answer with one JSON object and nothing else:
{"proposals": [{"title": string, "what": one or two plain sentences, "connects": [refs], "cadence": "daily" | "weekly" | "on new booking" | ...}]}`,
      messages: [
        {
          role: "user",
          content: `${brief(seed, blueprint)}\n\nThe Blueprint suggests: ${[...blueprint.orchestrationSuggestions, ...blueprint.suggestedConnections].join("; ") || "nothing yet"}\nKnown refs: ${KNOWN_CONNECTIONS.join(", ")}`,
        },
      ],
      schema: orchestrationSchema,
      maxTokens: 2500,
      signal,
    });
    writeJson(join(deps.paths.build(build.id).root, "orchestration.json"), { proposedAt: now(), proposals: plan.proposals });
    deps.sessions.addMessage(root, {
      role: "assistant",
      agent: "smith",
      content: [
        "When it launches, here is how I would keep it running and growing:",
        ...plan.proposals.map(
          (proposal, index) =>
            `${index + 1}. ${proposal.title}: ${proposal.what}${proposal.connects.length ? ` Connects ${proposal.connects.join(", ")}.` : ""}${proposal.cadence ? ` Runs ${proposal.cadence}.` : ""}`,
        ),
        "Tell me which ones to connect. Nothing connects until you say so.",
      ].join("\n"),
    });
    deps.sessions.update(session.id, { status: "done", summary: `${items.length} components, ${previews.length} directions, ${plan.proposals.length} proposals` });
  } catch (error) {
    if (isAbort(error, signal)) {
      deps.sessions.update(session.id, { status: "cancelled" });
      return;
    }
    deps.sessions.update(session.id, { status: "failed", summary: "The design draft did not finish", error: messageOf(error) });
    deps.sessions.addMessage(root, {
      role: "assistant",
      agent: "smith",
      content: `I could not finish the design draft: ${messageOf(error)} The layers keep building; ask me to try the design again.`,
    });
  }
}

/** Read back the orchestration proposals (for Smith's chat context). */
export function readOrchestration(paths: DataPaths, buildId: string): OrchestrationProposal[] {
  const saved = readJson(join(paths.build(buildId).root, "orchestration.json"), z.object({ proposals: orchestrationSchema.shape.proposals }));
  return saved?.proposals ?? [];
}
