import { z } from "zod";
import {
  buildIdSchema,
  isoDateTimeSchema,
  layerIdSchema,
  sandboxRunIdSchema,
  testFlightIdSchema,
} from "./common.js";

/**
 * Sandbox runs, personas and test flights (SPEC flow step 8). Icarus brings
 * the build up in local Docker (app, aethernet Chromium with CDP, bots) and
 * agents play the staff user and the customer user.
 */

export const sandboxRunStatusSchema = z.enum([
  "starting",
  "running",
  "stopping",
  "stopped",
  "failed",
]);
export type SandboxRunStatus = z.infer<typeof sandboxRunStatusSchema>;

/** Why a sandbox could not start, in a form the UI can explain. */
export const sandboxFailureSchema = z.object({
  code: z.enum(["docker_unavailable", "port_taken", "compose_failed", "app_unhealthy", "cancelled", "internal"]),
  message: z.string().min(1).max(2000),
  /** For port_taken: which port. */
  port: z.number().int().optional(),
});
export type SandboxFailure = z.infer<typeof sandboxFailureSchema>;

export const sandboxRunSchema = z.object({
  id: sandboxRunIdSchema,
  buildId: buildIdSchema,
  status: sandboxRunStatusSchema,
  ports: z.object({
    app: z.number().int(),
    cdp: z.number().int(),
    viewer: z.number().int(),
  }),
  /** docker compose project name, e.g. 4ge-bld_abc. */
  composeProject: z.string().min(1).max(120),
  appUrl: z.string().url().optional(),
  viewerUrl: z.string().url().optional(),
  startedAt: isoDateTimeSchema,
  stoppedAt: isoDateTimeSchema.optional(),
  failure: sandboxFailureSchema.optional(),
});
export type SandboxRun = z.infer<typeof sandboxRunSchema>;

export const sandboxStatusSchema = z.object({
  docker: z.object({
    available: z.boolean(),
    version: z.string().max(200).optional(),
    /** Plain words when Docker is down or missing. */
    message: z.string().max(1000).optional(),
  }),
  ports: z.array(
    z.object({ name: z.string(), port: z.number().int(), free: z.boolean() }),
  ),
  active: z.array(sandboxRunSchema),
});
export type SandboxStatus = z.infer<typeof sandboxStatusSchema>;

export const personaRoleSchema = z.enum(["staff", "customer"]);
export type PersonaRole = z.infer<typeof personaRoleSchema>;

/**
 * One scripted move a persona makes before it explores on its own. Targets
 * are visible text, a label or a CSS selector; the bot tries them in that
 * order. Added by the sandbox builder (optional, backwards compatible).
 */
export const personaScriptStepSchema = z.object({
  action: z.enum(["goto", "click", "fill", "select", "press", "expect-text", "expect-path"]),
  /** goto: a path such as /book. click, fill, select: what to act on. expect-*: what must show. */
  target: z.string().min(1).max(400),
  /** fill: the text to type. select: the option. press: the key. */
  value: z.string().max(400).optional(),
  /** Plain words for the step list; defaults to a sentence built from the action. */
  label: z.string().max(400).optional(),
});
export type PersonaScriptStep = z.infer<typeof personaScriptStepSchema>;

export const personaSchema = z.object({
  id: z.string().min(1).max(80),
  role: personaRoleSchema,
  name: z.string().min(1).max(120),
  /** Who they are, one or two sentences the agent plays from. */
  brief: z.string().min(1).max(1200),
  goals: z.array(z.string().min(1).max(400)).min(1).max(20),
  /** Where this persona starts, a path such as /admin. Defaults to / (customer) or a staff path when the app has one. */
  entryPath: z.string().regex(/^\/[^\s]*$/, "entry paths start with /").max(400).optional(),
  /** Scripted moves played before the persona explores the app on its own. */
  script: z.array(personaScriptStepSchema).max(40).optional(),
});
export type Persona = z.infer<typeof personaSchema>;

export const testStepStatusSchema = z.enum(["pending", "running", "passed", "failed", "skipped"]);

export const testStepSchema = z.object({
  id: z.string().min(1).max(80),
  personaId: z.string().min(1).max(80),
  /** What the persona does, in plain words. */
  action: z.string().min(1).max(1000),
  expectation: z.string().min(1).max(1000),
  status: testStepStatusSchema,
  /** Build-relative path of a screenshot under the run's evidence folder. */
  screenshot: z.string().max(400).optional(),
  error: z.string().max(2000).optional(),
  layerId: layerIdSchema.optional(),
  at: isoDateTimeSchema.optional(),
});
export type TestStep = z.infer<typeof testStepSchema>;

export const testFlightStatusSchema = z.enum(["queued", "running", "passed", "failed", "cancelled"]);
export type TestFlightStatus = z.infer<typeof testFlightStatusSchema>;

export const testFlightSchema = z.object({
  id: testFlightIdSchema,
  buildId: buildIdSchema,
  runId: sandboxRunIdSchema,
  status: testFlightStatusSchema,
  personas: z.array(personaSchema).min(1),
  steps: z.array(testStepSchema),
  /** Per layer verdicts; every layer green means the build may join Apps. */
  layers: z.array(
    z.object({ layerId: layerIdSchema, status: z.enum(["passed", "failed", "not-covered"]) }),
  ),
  summary: z.string().max(4000).optional(),
  startedAt: isoDateTimeSchema,
  finishedAt: isoDateTimeSchema.optional(),
});
export type TestFlight = z.infer<typeof testFlightSchema>;

export const startSandboxRequestSchema = z.object({ buildId: buildIdSchema });
export const stopSandboxRequestSchema = z.object({ runId: sandboxRunIdSchema });
export const startTestFlightRequestSchema = z.object({
  buildId: buildIdSchema,
  /** Defaults to the build's running sandbox. */
  runId: sandboxRunIdSchema.optional(),
  /** Defaults to one staff and one customer persona from the Blueprint. */
  personas: z.array(personaSchema).max(8).optional(),
});
export type StartTestFlightRequest = z.infer<typeof startTestFlightRequestSchema>;
