import { z } from "zod";

/**
 * Shared primitives for every 4GE contract. Browser safe: nothing here
 * touches node built-ins, so the studio can import the same schemas the
 * server validates with.
 */

export const isoDateTimeSchema = z.string().datetime({ offset: true });
export type IsoDateTime = z.infer<typeof isoDateTimeSchema>;

/** kebab-case slug, the stable tail of every typed id. */
export const slugSchema = z
  .string()
  .min(1)
  .max(80)
  .regex(/^[a-z0-9][a-z0-9-]*$/, "slug must be lowercase kebab-case");

/**
 * Runtime record ids carry a short prefix so a log line says what it names:
 * seed_, bld_, ses_, msg_, spr_, chk_, lyr_, rcp_, run_, tfl_, ann_, twk_,
 * ds_, cmp_. The tail is a nanoid or any url-safe token.
 */
export const ID_PREFIXES = {
  seed: "seed",
  build: "bld",
  session: "ses",
  message: "msg",
  sprint: "spr",
  checkpoint: "chk",
  layer: "lyr",
  receipt: "rcp",
  sandboxRun: "run",
  testFlight: "tfl",
  annotation: "ann",
  tweak: "twk",
  designSystem: "ds",
  component: "cmp",
  event: "evt",
} as const;
export type IdKind = keyof typeof ID_PREFIXES;

export function idSchemaFor(kind: IdKind) {
  const prefix = ID_PREFIXES[kind];
  return z
    .string()
    .min(prefix.length + 2)
    .max(120)
    .regex(
      new RegExp(`^${prefix}_[A-Za-z0-9_-]+$`),
      `id must look like ${prefix}_<token>`,
    );
}

/** Join a prefix and a token. Callers supply the token (nanoid in the server). */
export function makeId(kind: IdKind, token: string): string {
  return `${ID_PREFIXES[kind]}_${token}`;
}

export const seedIdSchema = idSchemaFor("seed");
export const buildIdSchema = idSchemaFor("build");
export const sessionIdSchema = idSchemaFor("session");
export const messageIdSchema = idSchemaFor("message");
export const sprintIdSchema = idSchemaFor("sprint");
export const checkpointIdSchema = idSchemaFor("checkpoint");
export const layerIdSchema = idSchemaFor("layer");
export const receiptIdSchema = idSchemaFor("receipt");
export const sandboxRunIdSchema = idSchemaFor("sandboxRun");
export const testFlightIdSchema = idSchemaFor("testFlight");
export const annotationIdSchema = idSchemaFor("annotation");
export const tweakIdSchema = idSchemaFor("tweak");
export const designSystemIdSchema = idSchemaFor("designSystem");
export const componentIdSchema = idSchemaFor("component");

/** Ids for user-visible catalog records are typed refs: mod:dev, adapter:railway. */
export function refSchemaFor(kind: string) {
  return z
    .string()
    .max(160)
    .regex(
      new RegExp(`^${kind}:[a-z0-9][a-z0-9._-]*$`),
      `ref must look like ${kind}:<slug>`,
    );
}

/** Build-relative path: no drive letter, no leading slash, no parent hops. */
export const relativePathSchema = z
  .string()
  .min(1)
  .max(400)
  .refine(
    (value) =>
      !value.startsWith("/") &&
      !value.startsWith("\\") &&
      !/^[A-Za-z]:/.test(value) &&
      !value.split(/[\\/]/).includes(".."),
    { message: "path must be relative to its build, with no parent hops" },
  );

/** The one JSON error shape every 4GE route returns on failure. */
export const API_ERROR_CODES = [
  "bad_request",
  "not_found",
  "not_implemented",
  "conflict",
  "gated",
  "cancelled",
  "model_unavailable",
  "model_error",
  "docker_unavailable",
  "port_taken",
  "credential_missing",
  "upstream_error",
  "internal",
] as const;
export const apiErrorCodeSchema = z.enum(API_ERROR_CODES);
export type ApiErrorCode = z.infer<typeof apiErrorCodeSchema>;

export const apiErrorSchema = z.object({
  error: z.object({
    code: apiErrorCodeSchema,
    /** Plain words a person can act on. No stack traces, no secrets. */
    message: z.string().min(1).max(2000),
    /** What to do next, when there is a clear next step. */
    hint: z.string().max(1000).optional(),
    detail: z.unknown().optional(),
  }),
});
export type ApiError = z.infer<typeof apiErrorSchema>;

/** Default HTTP status for each error code; the server uses this table. */
export const API_ERROR_STATUS: Record<ApiErrorCode, number> = {
  bad_request: 400,
  not_found: 404,
  not_implemented: 501,
  conflict: 409,
  gated: 409,
  cancelled: 409,
  model_unavailable: 503,
  model_error: 502,
  docker_unavailable: 503,
  port_taken: 409,
  credential_missing: 412,
  upstream_error: 502,
  internal: 500,
};

export const okSchema = z.object({ ok: z.literal(true) });
