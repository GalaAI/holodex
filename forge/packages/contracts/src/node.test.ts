import { homedir } from "node:os";
import { join, resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { ainboxQueueDir, ainboxSeedDropDir, dataPaths, dataRoot, resolveInBuild } from "./node.js";

describe("dataRoot", () => {
  it("uses %LOCALAPPDATA%\\Holodex\\4ge on Windows", () => {
    expect(dataRoot({ LOCALAPPDATA: "C:\\Users\\u\\AppData\\Local" }, "win32")).toBe(
      join("C:\\Users\\u\\AppData\\Local", "Holodex", "4ge"),
    );
  });

  it("uses ~/.4ge elsewhere and honors FORGE_DATA_ROOT everywhere", () => {
    expect(dataRoot({}, "linux")).toBe(join(homedir(), ".4ge"));
    expect(dataRoot({ FORGE_DATA_ROOT: "some/where" }, "win32")).toBe(resolve("some/where"));
  });

  it("finds the AInbox queue Holodex reads", () => {
    expect(ainboxQueueDir({ LOCALAPPDATA: "L" }, "win32")).toBe(join("L", "Omniwand", "ainbox-queue"));
  });

  it("drops later Seeds in the 4ge folder Holodex watches", () => {
    expect(ainboxSeedDropDir({ LOCALAPPDATA: "L" }, "win32")).toBe(join("L", "Omniwand", "ainbox-queue", "4ge"));
  });
});

describe("dataPaths", () => {
  const paths = dataPaths(resolve("root"));

  it("lays out build folders under builds/<id>", () => {
    const build = paths.build("bld_abc123");
    expect(build.record).toBe(join(resolve("root"), "builds", "bld_abc123", "build.json"));
    expect(build.receipts).toBe(join(resolve("root"), "builds", "bld_abc123", "receipts"));
  });

  it("refuses ids and paths that could escape the data root", () => {
    expect(() => paths.build("../../etc")).toThrow();
    const build = paths.build("bld_abc123");
    expect(() => resolveInBuild(build, "../outside.txt")).toThrow();
    expect(resolveInBuild(build, "src/index.ts")).toBe(join(build.app, "src/index.ts"));
  });
});
