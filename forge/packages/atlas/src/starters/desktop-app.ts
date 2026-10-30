import { STARTER_STATE, defineBlueprint, layer, nameParameters } from "./define.js";

/** Desktop app on Electron for Windows, Linux and macOS. */
export const DESKTOP_APP_BLUEPRINT = defineBlueprint({
  blueprintId: "blueprint:desktop-app",
  name: "Desktop app (Electron)",
  version: "1.0.0",
  state: STARTER_STATE,
  origin: "starter",
  kind: "desktop-app",
  platforms: ["windows", "linux", "macos"],
  description:
    "A desktop app for Windows, Linux and macOS on Electron and TypeScript: a secure main process with a small typed bridge, local data in the user's app folder, native menus and file access, auto updates, and installers built with electron-builder.",
  starter: "desktop-app",
  parameters: nameParameters("Notes that stay on your machine."),
  dependencies: [
    { name: "electron", kind: "npm-package", scope: "runtime", versionRange: "^45.1.0", required: true, role: "Desktop runtime with Chromium and Node" },
    { name: "electron-builder", kind: "development-tool", scope: "deployment", versionRange: "^26.18.0", required: true, role: "Installers for Windows, macOS and Linux" },
    { name: "vite", kind: "npm-package", scope: "build", versionRange: "^8.3.5", required: true, role: "Builds the renderer page" },
    { name: "typescript", kind: "npm-package", scope: "build", versionRange: "^7.0.2", required: true, role: "Types the main, preload and renderer code" },
    { name: "GitHub Releases", kind: "deployment-target", scope: "deployment", required: false, role: "Hosts installers and auto-update feeds on the user's account" },
  ],
  stages: [
    {
      stage: "architecture",
      goal: "Know the job {{appName}} does on the desktop, then the processes and data behind it.",
      layers: [
        layer("desktop-jobs", "research", "metis", "Desktop jobs", "What people do with the app, which files and system features it touches, and which OSes matter most.", ["Each job names the files or system features it needs"], { deliverables: ["docs/jobs.md"] }),
        layer("local-data", "data", "hephaestus", "Local data", "What the app stores in the user's app data folder, its format, and how it migrates between versions.", ["Data survives an update", "Bad data on disk does not crash the app"], { dependsOn: ["desktop-jobs"] }),
        layer("main-process", "backend", "hephaestus", "Main process services", "File access, menus, tray and windows in the main process, following Electron's security checklist.", ["contextIsolation and sandbox are on", "nodeIntegration is off", "External links open in the browser"], { dependsOn: ["local-data"], deliverables: ["electron/main.ts"] }),
      ],
      checkpoint: { title: "Jobs and data", question: "Which system features should it use: files, tray, notifications, shortcuts?" },
    },
    {
      stage: "orchestration",
      goal: "Connect the page to the main process and keep the app up to date.",
      layers: [
        layer("ipc-bridge", "orchestration", "athena", "Typed bridge", "A small preload bridge with typed calls; the main process validates every argument.", ["The renderer has no direct Node access", "Invalid arguments are refused"], { dependsOn: ["main-process"], deliverables: ["electron/preload.ts"] }),
        layer("auto-update", "integrations", "athena", "Auto updates", "Update checks against the user's release feed, with the user choosing when to restart.", ["No update installs without the user's go-ahead"], { dependsOn: ["main-process"] }),
      ],
      checkpoint: { title: "Bridge and updates", question: "Where should updates come from?" },
    },
    {
      stage: "interface",
      goal: "Agree on the look, then build the windows.",
      layers: [
        layer("design-system", "design-system", "smith", "Design system", "Tokens that sit well beside each OS, with light and dark following the system.", ["Text meets WCAG AA contrast"]),
        layer("windows", "interface", "smith", "Windows and menus", "Main window, settings and about, native menus and keyboard shortcuts.", ["Every action has a keyboard shortcut or menu item", "Window size and position are remembered"], { dependsOn: ["design-system", "ipc-bridge"], deliverables: ["src/main.ts"] }),
        layer("help-copy", "content", "metis", "Help and copy", "Labels, empty states, an about box and a short getting-started guide.", ["No placeholder text is left"], { dependsOn: ["desktop-jobs"] }),
      ],
      checkpoint: { title: "Windows", question: "Try the windows: what should change?" },
    },
    {
      stage: "deploy",
      goal: "Test in the sandbox, then build installers for each OS.",
      layers: [
        layer("sandbox-launch", "deploy", "icarus", "Sandbox launch", "Build, then run the renderer in the sandbox browser with the bridge stubbed, and the packaged Linux build headless.", ["pnpm build passes", "The renderer loads with no console errors"], { dependsOn: ["windows"] }),
        layer("test-flight-customer", "test-flight", "icarus", "Test flight: user", "A user bot adds, edits and removes data, restarts the app and checks it is still there.", ["Data survives a restart"], { dependsOn: ["sandbox-launch"] }),
        layer("test-flight-staff", "test-flight", "icarus", "Test flight: support", "A support bot checks the empty and error states and that external links leave the app.", ["Each state shows plain words and a way forward"], { dependsOn: ["sandbox-launch"] }),
        layer("installers", "deploy", "icarus", "Installers", "electron-builder installers for Windows, macOS and Linux; code signing waits for the user's certificates.", ["The Linux and Windows installers build", "Unsigned builds say so plainly"], { dependsOn: ["test-flight-customer", "test-flight-staff"] }),
      ],
      checkpoint: { title: "Installers", question: "Test flights are green. Build installers now?" },
    },
  ],
  suggestedConnections: ["mod:launch", "mod:pc", "adapter:docker", "augment:docker-mcp-gateway"],
  orchestration: [
    { key: "releases", title: "Releases", agent: "icarus", cadence: "on-event", description: "Builds installers, writes release notes and publishes to the release feed when the user approves.", connections: ["mod:launch"] },
    { key: "crash-reports", title: "Crash reports", agent: "icarus", cadence: "daily", description: "Collects crash reports the user opted into and reports new ones in plain words.", connections: [] },
    { key: "support", title: "Support inbox", agent: "metis", cadence: "weekly", description: "Groups support messages into fixes and ideas for the next version.", connections: [] },
  ],
  designSystem: {
    name: "Native Quiet",
    source: "kit",
    summary: "Quiet surfaces that sit beside the OS; follows system light and dark.",
    tokens: {
      colors: [
        { name: "bg", value: "#0b0d0e", role: "background" },
        { name: "surface", value: "#15191b", role: "surface" },
        { name: "text", value: "#e8e2d0", role: "text" },
        { name: "primary", value: "#79c4b7", role: "primary" },
      ],
      typography: [{ name: "body", family: "system-ui, sans-serif", size: "14px", weight: 400 }],
    },
    principles: ["Keyboard first", "Respect the OS", "Data stays on the machine unless the user says otherwise"],
  },
  sourceRefs: ["https://www.electronjs.org/docs/latest/tutorial/security", "https://www.electron.build/"],
  tags: ["desktop", "desktop app", "windows", "mac", "macos", "linux", "electron", "tauri", "offline", "native", "tray", "files"],
});
