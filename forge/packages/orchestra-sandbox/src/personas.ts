import { personaSchema, type Blueprint, type BuildKind, type Persona } from "@4ge/contracts";

/**
 * The people the sandbox bots play (SPEC flow step 8): a staff user who runs
 * the thing and a customer user who uses it. A Blueprint may name its own
 * personas (Blueprint.personas, with entry paths and scripted moves); when it
 * names none, Icarus derives one of each from the build kind.
 */

/** Paths the staff bot tries, in order, when its persona names no entry path. */
export const STAFF_PATHS = ["/admin", "/dashboard", "/staff", "/manage", "/login", "/account"] as const;

interface KindWords {
  thing: string;
  customer: { name: string; brief: string; goals: string[] };
  staff: { name: string; brief: string; goals: string[] };
}

const WORDS: Record<BuildKind, KindWords> = {
  website: {
    thing: "site",
    customer: {
      name: "First-time visitor",
      brief: "Someone who just found the site and wants to know what is offered and how to get in touch or book.",
      goals: ["Read the home page", "Find the services or products", "Reach the contact or booking form and send it"],
    },
    staff: {
      name: "Owner",
      brief: "The person who runs the business and checks the site before sharing it with customers.",
      goals: ["Open every page from the navigation", "Check each page reads clearly", "Find where customer requests arrive"],
    },
  },
  "web-app": {
    thing: "app",
    customer: {
      name: "New user",
      brief: "Someone who opens the app for the first time to get one job done.",
      goals: ["Understand what the app does from the first screen", "Start the main task", "Fill in and send the main form"],
    },
    staff: {
      name: "Staff member",
      brief: "Someone on the team who manages what users create in the app.",
      goals: ["Open the admin or dashboard area", "Review the records users created", "Change a setting"],
    },
  },
  "mobile-app": {
    thing: "app",
    customer: {
      name: "Phone user",
      brief: "Someone trying the app on a phone-sized screen for the first time.",
      goals: ["Get through the first screen", "Start the main task", "Send the main form"],
    },
    staff: {
      name: "Support staff",
      brief: "Someone who helps users and manages their data behind the app.",
      goals: ["Open the admin area", "Find a user's records", "Answer a request"],
    },
  },
  game: {
    thing: "game",
    customer: {
      name: "Player",
      brief: "Someone who opens the game to play a first round.",
      goals: ["Start the game", "Use the main controls", "See the score or result"],
    },
    staff: {
      name: "Game operator",
      brief: "Someone who runs the game, its settings and its leaderboard.",
      goals: ["Open the settings or admin screen", "Check the leaderboard or stats", "Change a setting"],
    },
  },
  erp: {
    thing: "system",
    customer: {
      name: "Employee",
      brief: "Someone who uses the system every day to record their work.",
      goals: ["Find their tasks", "Create a record", "Send the record form"],
    },
    staff: {
      name: "Administrator",
      brief: "Someone who manages users, settings and reports for the whole system.",
      goals: ["Open the admin area", "Review the reports", "Manage users or settings"],
    },
  },
  crm: {
    thing: "CRM",
    customer: {
      name: "Customer",
      brief: "A customer who reaches the business through the CRM's public pages or portal.",
      goals: ["Find how to contact the business", "Send a request", "See that the request was received"],
    },
    staff: {
      name: "Sales rep",
      brief: "Someone on the team who works the contacts and requests in the CRM.",
      goals: ["Open the contact list or pipeline", "Open a contact", "Log a note or follow-up"],
    },
  },
  "desktop-app": {
    thing: "app",
    customer: {
      name: "User",
      brief: "Someone using the app's main window for the first time.",
      goals: ["Understand the main window", "Start the main task", "Save or send the result"],
    },
    staff: {
      name: "Power user",
      brief: "Someone who sets the app up for others and manages its settings.",
      goals: ["Open the settings", "Change a setting", "Check every screen opens"],
    },
  },
  other: {
    thing: "build",
    customer: {
      name: "User",
      brief: "Someone using the build for what it was made for.",
      goals: ["Open the first screen", "Do the main task", "Send any form"],
    },
    staff: {
      name: "Operator",
      brief: "Someone who runs the build and checks it works.",
      goals: ["Open every screen", "Find the admin or settings area", "Check nothing errors"],
    },
  },
};

/** One staff and one customer persona for a build kind. */
export function defaultPersonas(kind: BuildKind = "website"): Persona[] {
  const words = WORDS[kind] ?? WORDS.other;
  return [
    personaSchema.parse({ id: "staff", role: "staff", ...words.staff }),
    personaSchema.parse({ id: "customer", role: "customer", ...words.customer }),
  ];
}

/**
 * The personas for a test flight: the request's as given, else the
 * Blueprint's, else the defaults for the build kind. A Blueprint list without
 * a staff or a customer gets the missing default added, so both sides of the
 * app are flown unless the caller asked for fewer.
 */
export function personasFor(options: {
  requested?: readonly Persona[] | undefined;
  blueprint?: Pick<Blueprint, "kind" | "personas"> | undefined;
  kind?: BuildKind | undefined;
}): Persona[] {
  const kind = options.blueprint?.kind ?? options.kind ?? "website";
  const chosen: Persona[] = [];
  if (options.requested?.length) {
    chosen.push(...options.requested);
  } else {
    chosen.push(...(options.blueprint?.personas ?? []));
    for (const fallback of defaultPersonas(kind)) {
      if (!chosen.some((persona) => persona.role === fallback.role)) chosen.push(fallback);
    }
  }
  const ids = new Set<string>();
  return chosen.map((persona) => {
    let id = persona.id;
    let suffix = 2;
    while (ids.has(id)) id = `${persona.id}-${suffix++}`;
    ids.add(id);
    return personaSchema.parse({ ...persona, id });
  });
}
