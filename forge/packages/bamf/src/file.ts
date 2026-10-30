import { readFile } from "node:fs/promises";
import { parse as parseYaml } from "yaml";
import { validateBamfCommand, type BamfCommand } from "./schema.js";

/**
 * Reading .bamf files. A file holds JSON (Ark's *.bamf.json) or YAML, and
 * one of three shapes: a single command, an array of commands, or a script
 * object `{ "commands": [...] }`.
 */
export interface BamfFileIssue {
  index: number;
  id?: string;
  issues: string[];
}

export interface BamfFileResult {
  ok: boolean;
  commands: BamfCommand[];
  problems: BamfFileIssue[];
}

export function parseBamfText(text: string, fileName = "input"): unknown {
  const trimmed = text.trim();
  if (trimmed.length === 0) throw new Error(`${fileName} is empty`);
  if (trimmed.startsWith("{") || trimmed.startsWith("[")) {
    try {
      return JSON.parse(trimmed);
    } catch (error) {
      throw new Error(`${fileName} is not valid JSON: ${error instanceof Error ? error.message : String(error)}`);
    }
  }
  try {
    return parseYaml(trimmed);
  } catch (error) {
    throw new Error(`${fileName} is not valid YAML: ${error instanceof Error ? error.message : String(error)}`);
  }
}

export function commandsFrom(document: unknown): unknown[] {
  if (Array.isArray(document)) return document;
  if (document && typeof document === "object" && Array.isArray((document as { commands?: unknown }).commands)) {
    return (document as { commands: unknown[] }).commands;
  }
  return [document];
}

export function validateBamfDocument(document: unknown): BamfFileResult {
  const commands: BamfCommand[] = [];
  const problems: BamfFileIssue[] = [];
  commandsFrom(document).forEach((candidate, index) => {
    const result = validateBamfCommand(candidate);
    if (result.ok && result.command) commands.push(result.command);
    else {
      const id =
        candidate && typeof candidate === "object" && typeof (candidate as { id?: unknown }).id === "string"
          ? (candidate as { id: string }).id
          : undefined;
      problems.push({ index, ...(id ? { id } : {}), issues: result.issues });
    }
  });
  const ids = new Map<string, number>();
  commands.forEach((command, index) => {
    const seen = ids.get(command.id);
    if (seen !== undefined) {
      problems.push({ index, id: command.id, issues: [`id: duplicates command ${seen + 1}`] });
    }
    ids.set(command.id, index);
  });
  return { ok: problems.length === 0, commands, problems };
}

export async function readBamfFile(path: string): Promise<unknown> {
  const text = await readFile(path, "utf8");
  return parseBamfText(text, path);
}
