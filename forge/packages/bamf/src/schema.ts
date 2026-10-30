// Copied from GalaAI/ark packages/bamf-schema/src/index.ts at 0f4b55c6 for @4ge/bamf.
// Changes for 4GE: the target layer enum is built from BAMF_TARGET_LAYERS
// (4GE system layers) instead of @ark/shared-types ARK_LAYERS, the
// BamfTargetLayer intersection with ArkLayer is dropped, and the creative
// timeline actions (timeline-actions.ts) are not carried because no 4GE
// layer emits them. Everything else, including v0.1 and v0.2 envelopes and
// validateBamfCommand, is unchanged.
import { z } from "zod";
import { BAMF_TARGET_LAYERS } from "./layers.js";

// Built from BAMF_TARGET_LAYERS (layers.ts) rather than restated here, so
// the 4GE layer list and the envelope cannot drift.
export const bamfTargetLayerSchema = z.enum(BAMF_TARGET_LAYERS);

export const bamfIntentSchema = z.enum([
  "read",
  "plan",
  "validate",
  "execute",
  "schedule",
  "sync",
]);

export const bamfRiskLevelSchema = z.enum([
  "low",
  "medium",
  "high",
  "critical",
]);

export const bamfHashSchema = z
  .string()
  .regex(/^[a-f0-9]{64}$/, "hash must be a lowercase SHA-256 digest");

export const bamfRecordSnapshotSchema = z
  .object({
    ref: z.string().min(3).max(240),
    revision: z.number().int().nonnegative(),
    contentHash: bamfHashSchema,
  })
  .strict();

export const bamfEffectProfileSchema = z
  .object({
    state: z.enum(["read_only", "mutating"]),
    externality: z.enum(["internal", "external"]),
    reversibility: z.enum([
      "reversible",
      "compensatable",
      "irreversible",
      "unknown",
    ]),
    dataSensitivity: z.enum([
      "public",
      "internal",
      "confidential",
      "credential",
    ]),
    financialImpact: z.enum(["none", "bounded", "unbounded"]),
    environment: z.enum([
      "sandbox",
      "local",
      "staging",
      "production",
      "external",
    ]),
    autonomy: z.enum(["advisory", "single_action", "workflow"]),
    infrastructure: z.enum(["none", "configuration", "deployment"]),
    credentialAccess: z.enum(["none", "use", "manage"]),
    destructive: z.boolean(),
  })
  .strict();

export const bamfArgumentSourceSchema = z.enum([
  "operator",
  "system",
  "registry",
  "retrieved",
]);

export const bamfDataClassificationSchema = z.enum([
  "public",
  "internal",
  "confidential",
  "credential",
]);

export const bamfArgumentProvenanceSchema = z
  .object({
    source: bamfArgumentSourceSchema,
    sourceRef: z.string().min(3).max(500),
    classification: bamfDataClassificationSchema,
  })
  .strict();

export const bamfOperatorGateSchema = z.object({
  required: z.boolean(),
  reason: z.string().min(1).max(400).optional(),
  approvedBy: z.string().min(1).max(120).optional(),
  approvedAt: z.string().datetime().optional(),
});

export const bamfPermissionSourceSchema = z.enum([
  "campaign_cast",
  "symphony_cast",
  "ritual_cast",
  "spell_cast",
  "item_use",
  "owner_session",
  "configuration",
]);

export const bamfPermissionEnvelopeSchema = z
  .object({
    permitId: z.string().min(3).max(160),
    source: bamfPermissionSourceSchema,
    scopeRef: z.string().min(3).max(240),
    ownerIdentity: z.string().min(1).max(160),
    allowedActions: z.array(z.string().min(1).max(160)).min(1).max(160),
    allowedTargets: z.array(z.string().min(1).max(160)).min(1).max(80),
    issuedAt: z.string().datetime(),
    expiresAt: z.string().datetime(),
    policyRefs: z.array(z.string().min(1).max(240)).max(80).default([]),
    constraints: z
      .object({
        brandRef: z.string().min(1).max(160).optional(),
        voiceRef: z.string().min(1).max(240).optional(),
        allowDestructiveActions: z.boolean().default(false),
        maxSpendCents: z.number().int().min(0).max(100_000_000).default(0),
      })
      .default({
        allowDestructiveActions: false,
        maxSpendCents: 0,
      }),
  })
  .strict()
  .superRefine((permission, context) => {
    if (Date.parse(permission.expiresAt) <= Date.parse(permission.issuedAt)) {
      context.addIssue({
        code: "custom",
        path: ["expiresAt"],
        message: "expiresAt must be later than issuedAt",
      });
    }
  });

const bamfCommandBaseShape = {
  id: z.string().min(3).max(120),
  intent: bamfIntentSchema,
  targetLayer: bamfTargetLayerSchema,
  target: z.string().min(1).max(160),
  action: z.string().min(1).max(160),
  args: z.record(z.string(), z.unknown()).default({}),
  risk: bamfRiskLevelSchema.default("low"),
  operatorGate: bamfOperatorGateSchema.default({ required: false }),
  permission: bamfPermissionEnvelopeSchema.optional(),
  metadata: z
    .object({
      source: z.string().min(1).max(160).optional(),
      correlationId: z.string().min(1).max(160).optional(),
      createdAt: z.string().datetime().optional(),
    })
    .default({}),
} satisfies z.ZodRawShape;

export const bamfCommandV01Schema = z
  .object({
    version: z.literal("0.1"),
    ...bamfCommandBaseShape,
  })
  .strict();

export const bamfCommandV02Schema = z
  .object({
    version: z.literal("0.2"),
    ...bamfCommandBaseShape,
    effects: bamfEffectProfileSchema,
    execution: z
      .object({
        commandDefinitionRef: z.string().min(3).max(240),
        commandDefinitionHash: bamfHashSchema,
        requiredCapabilityRefs: z
          .array(z.string().min(3).max(240))
          .max(160)
          .default([]),
        argumentProvenance: z.record(
          z.string().min(1).max(160),
          bamfArgumentProvenanceSchema,
        ),
        externalSinkRefs: z
          .array(z.string().min(3).max(240))
          .max(80)
          .default([]),
        controlFlowOwner: z.literal("registry-command-definition"),
        preconditionRefs: z
          .array(z.string().min(3).max(240))
          .max(80)
          .default([]),
        worldStateRequirementRefs: z
          .array(z.string().min(3).max(240))
          .max(80)
          .default([]),
      })
      .strict(),
    semantic: z
      .object({
        candidateRef: z.string().min(3).max(240),
        candidateHash: bamfHashSchema,
        verificationReceiptRef: z.string().min(3).max(240),
        verificationReceiptHash: bamfHashSchema,
        registrySnapshotRef: z.string().min(3).max(240),
        registrySnapshotHash: bamfHashSchema,
        resolvedRecords: z.array(bamfRecordSnapshotSchema).min(1).max(240),
        policyRecords: z.array(bamfRecordSnapshotSchema).max(80).default([]),
        compilerVersion: z.string().regex(/^\d+\.\d+\.\d+$/),
        verifierVersion: z.string().regex(/^\d+\.\d+\.\d+$/),
        normalizationVersion: z.string().regex(/^\d+\.\d+\.\d+$/),
        exactInputDigest: bamfHashSchema,
        normalizedInputDigest: bamfHashSchema,
        sourceEnvelopeRef: z.string().min(3).max(500).optional(),
      })
      .strict(),
  })
  .strict()
  .superRefine((command, context) => {
    const argumentKeys = Object.keys(command.args).sort();
    const provenanceKeys = Object.keys(
      command.execution.argumentProvenance,
    ).sort();
    if (argumentKeys.join("\u0000") !== provenanceKeys.join("\u0000")) {
      context.addIssue({
        code: "custom",
        path: ["execution", "argumentProvenance"],
        message:
          "argumentProvenance must describe every argument and no others",
      });
    }
  });

export const bamfCommandSchema = z.discriminatedUnion("version", [
  bamfCommandV01Schema,
  bamfCommandV02Schema,
]);

export type BamfTargetLayer = z.infer<typeof bamfTargetLayerSchema>;
export type BamfIntent = z.infer<typeof bamfIntentSchema>;
export type BamfRiskLevel = z.infer<typeof bamfRiskLevelSchema>;
export type BamfEffectProfile = z.infer<typeof bamfEffectProfileSchema>;
export type BamfArgumentSource = z.infer<typeof bamfArgumentSourceSchema>;
export type BamfDataClassification = z.infer<
  typeof bamfDataClassificationSchema
>;
export type BamfArgumentProvenance = z.infer<
  typeof bamfArgumentProvenanceSchema
>;
export type BamfRecordSnapshot = z.infer<typeof bamfRecordSnapshotSchema>;
export type BamfPermissionSource = z.infer<typeof bamfPermissionSourceSchema>;
export type BamfPermissionEnvelope = z.infer<
  typeof bamfPermissionEnvelopeSchema
>;
export type BamfCommand = z.infer<typeof bamfCommandSchema>;
export type BamfCommandV01 = z.infer<typeof bamfCommandV01Schema>;
export type BamfCommandV02 = z.infer<typeof bamfCommandV02Schema>;

export interface BamfValidationResult {
  ok: boolean;
  command?: BamfCommand;
  issues: string[];
}

export function validateBamfCommand(input: unknown): BamfValidationResult {
  const parsed = bamfCommandSchema.safeParse(input);

  if (parsed.success) {
    return {
      ok: true,
      command: parsed.data,
      issues: [],
    };
  }

  return {
    ok: false,
    issues: parsed.error.issues.map(
      (issue) => `${issue.path.join(".") || "command"}: ${issue.message}`,
    ),
  };
}

