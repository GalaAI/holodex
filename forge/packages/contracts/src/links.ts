import type { Session } from "./session.js";

/**
 * Studio deep links. Holodex opens the studio full screen in WebView2 on one of
 * these paths after a Library click has opened (POST /sessions) the item's
 * Smith session, so the studio shows that session's thread. Holodex mirrors
 * this in ForgeHost.StudioPathFor; keep the two in step.
 */
export const STUDIO_SESSION_PARAM = "session";

export function studioPathForSession(session: Pick<Session, "id" | "kind" | "buildId" | "subject">): string {
  const query = `?${STUDIO_SESSION_PARAM}=${encodeURIComponent(session.id)}`;
  if (session.kind === "imagistation") return `/imagistation/${encodeURIComponent(session.id)}`;
  if (session.subject?.type === "mod") return `/mods/${encodeURIComponent(session.subject.id)}${query}`;
  if (session.buildId) return `/ide/${encodeURIComponent(session.buildId)}${query}`;
  return `/library${query}`;
}
