import { STARTER_STATE, defineBlueprint, layer, nameParameters } from "./define.js";

/** Web game on Phaser: plays in any browser, ships as a static build. */
export const GAME_BLUEPRINT = defineBlueprint({
  blueprintId: "blueprint:web-game",
  name: "Web game (Phaser)",
  version: "1.0.0",
  state: STARTER_STATE,
  origin: "starter",
  kind: "game",
  platforms: ["web"],
  description:
    "A browser game on Phaser 4 with Vite and TypeScript: a game design doc, rules kept apart from scenes so they can be tested and tuned, scenes and HUD, input and sound, saves and an optional leaderboard, and a static build that hosts anywhere.",
  starter: "game",
  parameters: [...nameParameters("Catch what falls. Miss three and it is over."), { key: "genre", description: "The kind of game, e.g. arcade, puzzle, platformer", required: false, defaultValue: "arcade" }],
  dependencies: [
    { name: "phaser", kind: "npm-package", scope: "runtime", versionRange: "^4.2.1", required: true, role: "Rendering, scenes, input, physics and sound" },
    { name: "vite", kind: "npm-package", scope: "build", versionRange: "^8.3.5", required: true, role: "Builds and serves the game" },
    { name: "typescript", kind: "npm-package", scope: "build", versionRange: "^7.0.2", required: true, role: "Type-checks rules and scenes" },
    { name: "Cloudflare Pages or itch.io", kind: "deployment-target", scope: "deployment", required: false, role: "Hosts the static build" },
  ],
  stages: [
    {
      stage: "architecture",
      goal: "Pin down the core loop of {{appName}} and the rules behind it.",
      layers: [
        layer("game-design", "research", "metis", "Game design doc", "The {{genre}} core loop, win and lose conditions, controls, difficulty curve and two or three reference games.", ["The doc states the core loop in one sentence", "Controls cover keyboard and touch"], { deliverables: ["docs/design.md"] }),
        layer("rules", "data", "hephaestus", "Rules and tuning", "Scoring, lives, levels and difficulty as plain functions with the numbers in game.json.", ["Rules have tests", "Every tuning number lives in game.json"], { dependsOn: ["game-design"], deliverables: ["src/rules.ts", "src/game.json"] }),
        layer("saves", "backend", "hephaestus", "Saves and best scores", "Local saves and best scores that survive reloads; a hosted leaderboard only if the design calls for one.", ["Progress survives a reload", "Storage being off does not crash the game"], { dependsOn: ["rules"] }),
      ],
      checkpoint: { title: "Design", question: "Is this the game you want to play? What would make it more fun?" },
    },
    {
      stage: "orchestration",
      goal: "Wire input, sound and any online pieces.",
      layers: [
        layer("input-and-audio", "orchestration", "athena", "Input and sound", "Keyboard, pointer and touch mapped to the same actions; sound effects and music with a mute that is remembered.", ["Every action works with keyboard and touch", "Mute persists"], { dependsOn: ["rules"] }),
        layer("leaderboard", "integrations", "athena", "Online leaderboard (optional)", "Scores to the user's Supabase project with basic cheat checks; skipped for an offline game.", ["Offline games record this layer as skipped with a reason"], { dependsOn: ["saves"] }),
      ],
      checkpoint: { title: "Feel", question: "Do the controls feel right? Should scores go online?" },
    },
    {
      stage: "interface",
      goal: "Set the art direction, then build scenes and HUD.",
      layers: [
        layer("art-direction", "design-system", "smith", "Art direction", "Palette, shapes, type and motion rules for {{appName}}.", ["Score and lives stay readable on every background"]),
        layer("scenes-and-hud", "interface", "smith", "Scenes and HUD", "Title, play, pause and game over scenes, and the HUD.", ["Holds 60 frames per second on a mid-range laptop", "Resizes to fit any window"], { dependsOn: ["art-direction", "input-and-audio"], deliverables: ["src/main.ts"] }),
        layer("assets", "content", "metis", "Art and sound assets", "Sprites, sounds and music with their licenses recorded.", ["Every asset has a recorded license"], { dependsOn: ["game-design"] }),
      ],
      checkpoint: { title: "Look and play", question: "Play a round: what should change?" },
    },
    {
      stage: "deploy",
      goal: "Playtest in the sandbox, then publish.",
      layers: [
        layer("sandbox-launch", "deploy", "icarus", "Sandbox launch", "Build and serve the game in the sandbox on the app port.", ["pnpm build passes", "The game canvas loads without console errors"], { dependsOn: ["scenes-and-hud"] }),
        layer("test-flight-customer", "test-flight", "icarus", "Test flight: player", "A player bot plays several rounds with keyboard and with pointer, loses on purpose and restarts.", ["Score, lives and restart behave as the rules say"], { dependsOn: ["sandbox-launch"] }),
        layer("test-flight-staff", "test-flight", "icarus", "Test flight: designer", "A designer bot changes a tuning number in game.json, rebuilds and checks the game reflects it.", ["The change shows after a rebuild"], { dependsOn: ["sandbox-launch"] }),
        layer("publish", "deploy", "icarus", "Publish", "Publish the static build to the user's host through the Launch Mod.", ["The live URL loads and plays"], { dependsOn: ["test-flight-customer", "test-flight-staff"] }),
      ],
      checkpoint: { title: "Ship it", question: "Playtests are green. Publish now?" },
    },
  ],
  suggestedConnections: ["mod:launch", "adapter:cloudflare", "adapter:supabase", "augment:cloudflare-mcp"],
  orchestration: [
    { key: "devlog", title: "Devlog", agent: "metis", cadence: "weekly", description: "Drafts a short devlog with a clip or screenshot from what changed, for the user to post.", connections: [] },
    { key: "balance", title: "Balance check", agent: "smith", cadence: "weekly", description: "Reads play data and best scores and proposes tuning changes to game.json.", connections: ["mod:dev"] },
    { key: "uptime", title: "Uptime", agent: "icarus", cadence: "daily", description: "Checks the live game loads and plays.", connections: ["mod:launch"] },
  ],
  designSystem: {
    name: "Arcade Night",
    source: "kit",
    summary: "Dark field, bright shapes, big readable HUD.",
    tokens: {
      colors: [
        { name: "field", value: "#0b0d0e", role: "background" },
        { name: "player", value: "#79c4b7", role: "primary" },
        { name: "pickup", value: "#e8e2d0", role: "accent" },
        { name: "hud", value: "#e8e2d0", role: "text" },
      ],
      motion: [{ name: "hit", durationMs: 120, easing: "ease-out" }],
    },
    principles: ["Readable at a glance", "Feedback on every action", "Fair before hard"],
  },
  sourceRefs: ["https://docs.phaser.io/", "https://github.com/phaserjs/template-vite-ts"],
  tags: ["game", "video game", "browser game", "web game", "arcade", "puzzle", "platformer", "phaser", "three.js", "play", "level", "score"],
});
