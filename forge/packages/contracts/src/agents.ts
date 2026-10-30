import { z } from "zod";

/**
 * The 4GE roster (SPEC "What it is"). Smith is the one agent the user talks
 * to in the IDE; Prometheus guides Imagistation; the orchestra works in
 * nested sessions under Smith.
 *
 * Adapted from GalaAI/ark agents/shared/registry.json (surfaceBinding zone
 * 4ge) and agent-prometheus.manifest.json. Differences from Ark, by SPEC:
 * Hephaestus holds the architecture stage Ark gives to vulcan, Metis does
 * research, extraction, evidence and design inventory, and the interface
 * stage belongs to Smith working with the user.
 */
export const AGENT_IDS = [
  "prometheus",
  "smith",
  "hephaestus",
  "athena",
  "metis",
  "icarus",
] as const;
export const agentIdSchema = z.enum(AGENT_IDS);
export type AgentId = z.infer<typeof agentIdSchema>;

/** Which default model an agent runs on (see models.ts DEFAULT_MODELS). */
export const modelTierSchema = z.enum(["lead", "worker", "vision"]);
export type ModelTier = z.infer<typeof modelTierSchema>;

export interface AgentProfile {
  id: AgentId;
  name: string;
  /** One plain sentence: what this agent does for the user. */
  role: string;
  tier: ModelTier;
  /** True when the user talks to this agent directly. */
  userFacing: boolean;
}

export const AGENT_ROSTER: Record<AgentId, AgentProfile> = {
  prometheus: {
    id: "prometheus",
    name: "Prometheus",
    role: "Guides Imagistation: asks follow-up questions, picks or crafts a Blueprint, and writes the Seed Scroll. The user makes the calls.",
    tier: "lead",
    userFacing: true,
  },
  smith: {
    id: "smith",
    name: "Smith",
    role: "Runs the build in the IDE, works with the user on the design system and landing page, and delegates layers to the orchestra.",
    tier: "lead",
    userFacing: true,
  },
  hephaestus: {
    id: "hephaestus",
    name: "Hephaestus",
    role: "Builds the architecture and backend: schemas, data, services and the packages that define them.",
    tier: "worker",
    userFacing: false,
  },
  athena: {
    id: "athena",
    name: "Athena",
    role: "Builds orchestration, integrations and data flow between the backend and the interface.",
    tier: "worker",
    userFacing: false,
  },
  metis: {
    id: "metis",
    name: "Metis",
    role: "Researches, extracts references and evidence, and keeps the design and component inventory.",
    tier: "worker",
    userFacing: false,
  },
  icarus: {
    id: "icarus",
    name: "Icarus",
    role: "Launches the build in the sandbox, runs the staff and customer test flights, and prepares deploy.",
    tier: "worker",
    userFacing: false,
  },
};

/** Agents that work in nested sessions under Smith. */
export const ORCHESTRA: readonly AgentId[] = [
  "hephaestus",
  "athena",
  "metis",
  "icarus",
];
