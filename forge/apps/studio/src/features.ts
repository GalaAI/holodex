import type { ServerLayer } from "@4ge/contracts";
import {
  FlaskConical,
  Hammer,
  Library,
  Puzzle,
  SlidersHorizontal,
  Sparkles,
  type LucideIcon,
} from "lucide-react";
import type { ComponentType } from "react";

/**
 * The studio's feature folders. Each lives in src/features/<id>/index.tsx,
 * owned by the builder of that layer, default-exports one component, and is
 * lazy-loaded by the shell. `path` may end in an optional param.
 */
export interface FeatureDef {
  id: "imagistation" | "ide" | "tweak" | "mods" | "library" | "sandbox";
  label: string;
  /** Rail eyebrow, mono, // separated. */
  eyebrow: string;
  path: string;
  /** Where the rail link points. */
  href: string;
  icon: LucideIcon;
  /** Server layer slots whose routes this feature calls. */
  layers: ServerLayer[];
  load: () => Promise<{ default: ComponentType }>;
}

export const FEATURES: FeatureDef[] = [
  {
    id: "imagistation",
    label: "Imagistation",
    eyebrow: "4GE // IMAGISTATION",
    path: "/imagistation/:sessionId?",
    href: "/imagistation",
    icon: Sparkles,
    layers: ["orchestra", "atlas"],
    load: () => import("./features/imagistation/index.js"),
  },
  {
    id: "ide",
    label: "Build",
    eyebrow: "4GE // BUILD",
    path: "/ide/:buildId?",
    href: "/ide",
    icon: Hammer,
    layers: ["orchestra"],
    load: () => import("./features/ide/index.js"),
  },
  {
    id: "tweak",
    label: "Tweak",
    eyebrow: "4GE // TWEAK",
    path: "/tweak/:buildId?",
    href: "/tweak",
    icon: SlidersHorizontal,
    layers: ["design"],
    load: () => import("./features/tweak/index.js"),
  },
  {
    id: "sandbox",
    label: "Sandbox",
    eyebrow: "4GE // SANDBOX",
    path: "/sandbox/:buildId?",
    href: "/sandbox",
    icon: FlaskConical,
    layers: ["sandbox"],
    load: () => import("./features/sandbox/index.js"),
  },
  {
    id: "library",
    label: "Library",
    eyebrow: "4GE // LIBRARY",
    path: "/library",
    href: "/library",
    icon: Library,
    layers: ["library"],
    load: () => import("./features/library/index.js"),
  },
  {
    id: "mods",
    label: "Mods",
    eyebrow: "4GE // MODS",
    path: "/mods/:modId?",
    href: "/mods",
    icon: Puzzle,
    layers: ["mods"],
    load: () => import("./features/mods/index.js"),
  },
];
