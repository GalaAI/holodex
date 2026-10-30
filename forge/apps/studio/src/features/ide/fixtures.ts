import type { Build, Checkpoint, Session } from "@4ge/contracts";

/**
 * Test fixtures for the IDE tests only (never imported by app code). They
 * are shaped by the contracts, and every response that carries them is
 * validated by the real client in the tests.
 */
export const AT = "2026-10-30T12:00:00.000Z";

export function fixtureBuild(overrides: Partial<Build> = {}): Build {
  return {
    id: "bld_t1",
    seedId: "seed_t1",
    blueprintId: "blueprint:website",
    title: "Tattoo shop site",
    status: "awaiting-checkpoint",
    stages: [
      { stage: "architecture", owner: "hephaestus", status: "passed", sprintId: "spr_1" },
      { stage: "orchestration", owner: "athena", status: "running" },
      { stage: "interface", owner: "smith", status: "pending" },
      { stage: "deploy", owner: "icarus", status: "pending" },
    ],
    layers: [
      {
        id: "lyr_data",
        buildId: "bld_t1",
        key: "data-model",
        kind: "data",
        stage: "architecture",
        agent: "hephaestus",
        title: "Data model",
        status: "passed",
        dependsOn: [],
        previews: [
          { kind: "text", title: "Schema", ref: "previews/schema.txt", stage: "architecture", layerId: "lyr_data", updatedAt: AT },
        ],
        testFlight: "not-run",
        sessionId: "ses_heph",
        receiptId: "rcp_1",
      },
      {
        id: "lyr_flow",
        buildId: "bld_t1",
        key: "booking-flow",
        kind: "orchestration",
        stage: "orchestration",
        agent: "athena",
        title: "Booking flow",
        status: "running",
        dependsOn: ["data-model"],
        previews: [],
        testFlight: "not-run",
        sessionId: "ses_athena",
      },
    ],
    sprints: [
      { id: "spr_1", buildId: "bld_t1", index: 1, stage: "architecture", layerIds: ["lyr_data"], status: "passed", checkpointId: "chk_1" },
    ],
    openCheckpointId: "chk_1",
    rootSessionId: "ses_root",
    createdAt: AT,
    updatedAt: AT,
    ...overrides,
  };
}

export const CHECKPOINT: Checkpoint = {
  id: "chk_1",
  buildId: "bld_t1",
  sprintId: "spr_1",
  stage: "architecture",
  title: "Architecture is in",
  summary: "Hephaestus laid out the data model for bookings, artists and flash.",
  question: "Does the data model cover how your shop books?",
  previews: [],
  status: "open",
  createdAt: AT,
};

export const SESSIONS: Session[] = [
  {
    id: "ses_root",
    parentId: null,
    rootId: "ses_root",
    agent: "smith",
    kind: "build",
    task: "Build the tattoo shop site",
    status: "running",
    buildId: "bld_t1",
    createdAt: AT,
    updatedAt: AT,
  },
  {
    id: "ses_heph",
    parentId: "ses_root",
    rootId: "ses_root",
    agent: "hephaestus",
    kind: "layer",
    task: "Data model",
    status: "done",
    summary: "Schema written and checked",
    buildId: "bld_t1",
    createdAt: AT,
    updatedAt: AT,
    finishedAt: "2026-10-30T12:03:00.000Z",
  },
  {
    id: "ses_athena",
    parentId: "ses_root",
    rootId: "ses_root",
    agent: "athena",
    kind: "layer",
    task: "Booking flow",
    status: "running",
    summary: "Wiring the booking requests",
    buildId: "bld_t1",
    createdAt: "2026-10-30T12:04:00.000Z",
    updatedAt: AT,
  },
];
