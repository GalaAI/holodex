import { describe, expect, it } from "vitest";
import { STUDIO_SESSION_PARAM, studioPathForSession } from "./links.js";

describe("studioPathForSession", () => {
  it("opens a mod session on the mod's page", () => {
    expect(
      studioPathForSession({ id: "ses_a1", kind: "mod", subject: { type: "mod", id: "mod:dev" } }),
    ).toBe(`/mods/mod%3Adev?${STUDIO_SESSION_PARAM}=ses_a1`);
  });

  it("opens a build's session in the IDE", () => {
    expect(
      studioPathForSession({ id: "ses_b2", kind: "library", buildId: "bld_x9", subject: { type: "project", id: "bld_x9" } }),
    ).toBe("/ide/bld_x9?session=ses_b2");
  });

  it("opens Imagistation sessions by their own route and anything else on the Library", () => {
    expect(studioPathForSession({ id: "ses_c3", kind: "imagistation" })).toBe("/imagistation/ses_c3");
    expect(studioPathForSession({ id: "ses_d4", kind: "library", subject: { type: "adapter", id: "vercel" } })).toBe(
      "/library?session=ses_d4",
    );
  });
});
