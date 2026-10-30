import { STARTER_STATE, defineBlueprint, layer, nameParameters } from "./define.js";

/** Mobile app on Expo and React Native: one codebase for iOS, Android and the web. */
export const MOBILE_APP_BLUEPRINT = defineBlueprint({
  blueprintId: "blueprint:expo-mobile-app",
  name: "Mobile app (Expo)",
  version: "1.0.0",
  state: STARTER_STATE,
  origin: "starter",
  kind: "mobile-app",
  platforms: ["ios", "android", "web"],
  description:
    "A phone app built with Expo and React Native from one TypeScript codebase for iOS, Android and the web: screens and navigation, local data that works offline, optional sync to a backend, push notifications, and store builds through the user's own Expo account.",
  starter: "mobile-app",
  parameters: nameParameters("Keep track of what matters."),
  dependencies: [
    { name: "expo", kind: "npm-package", scope: "runtime", versionRange: "~58.0.21", required: true, role: "The app runtime, config and build tooling" },
    { name: "react-native", kind: "npm-package", scope: "runtime", versionRange: "0.88.1", required: true, role: "Native views on iOS and Android" },
    { name: "react-native-web", kind: "npm-package", scope: "runtime", versionRange: "^0.21.4", required: true, role: "The same app on the web, used for the sandbox test flights" },
    { name: "EAS Build", kind: "service", scope: "deployment", required: false, role: "Store builds on the user's own Expo account" },
    { name: "Supabase", kind: "service", scope: "runtime", required: false, role: "Sync and accounts when the app needs a backend" },
  ],
  stages: [
    {
      stage: "architecture",
      goal: "Know the moments people open {{appName}} for, and what it keeps.",
      layers: [
        layer("use-moments", "research", "metis", "Use moments", "When and where people open the app, the one thing each visit is for, and comparable apps worth learning from.", ["Each moment names its screen and the data it needs"], { deliverables: ["docs/moments.md"] }),
        layer("local-data", "data", "hephaestus", "Local data", "The records the app keeps on the device and how they survive restarts and updates.", ["Data survives an app restart", "Pure logic has tests"], { dependsOn: ["use-moments"], deliverables: ["src/items.ts"] }),
        layer("sync-api", "backend", "hephaestus", "Sync backend (optional)", "When accounts or sharing are needed, the tables and API in the user's Supabase project; skipped for a device-only app.", ["Device-only apps record this layer as skipped with a reason"], { dependsOn: ["local-data"] }),
      ],
      checkpoint: { title: "Moments and data", question: "Does it need accounts and sync, or can it live on the phone?" },
    },
    {
      stage: "orchestration",
      goal: "Connect data, sync and notifications.",
      layers: [
        layer("offline-sync", "orchestration", "athena", "Offline and sync", "Reads and writes go to the device first and sync when online; conflicts resolve by last edit with a visible note.", ["Works in airplane mode", "Sync resumes when the network returns"], { dependsOn: ["local-data"] }),
        layer("notifications", "integrations", "athena", "Push notifications", "Permission asked at the moment it helps, and reminders sent through Expo notifications.", ["Denying permission leaves the app fully usable"], { dependsOn: ["use-moments"] }),
      ],
      checkpoint: { title: "Data flow", question: "Which reminders or alerts should the app send?" },
    },
    {
      stage: "interface",
      goal: "Agree on the look, then build the screens.",
      layers: [
        layer("design-system", "design-system", "smith", "Design system", "Colors, type scale, touch targets and motion for {{appName}}.", ["Touch targets are at least 44 points", "Text meets WCAG AA contrast"]),
        layer("screens", "interface", "smith", "Screens and navigation", "Home, detail, add or edit, and settings, with the platform's back and gesture behavior.", ["Every screen works one-handed", "Screen readers announce every control"], { dependsOn: ["design-system", "offline-sync"], deliverables: ["App.tsx"] }),
        layer("store-copy", "content", "metis", "Copy and store listing", "In-app text, empty states, and the store description and screenshots list.", ["No placeholder text is left"], { dependsOn: ["use-moments"] }),
      ],
      checkpoint: { title: "Screens", question: "Try the screens: what feels off in the hand?" },
    },
    {
      stage: "deploy",
      goal: "Test the web build in the sandbox, then make store builds.",
      layers: [
        layer("sandbox-launch", "deploy", "icarus", "Sandbox launch", "Export the web build and serve it in the sandbox on the app port.", ["pnpm build passes", "The sandbox serves the app"], { dependsOn: ["screens"] }),
        layer("test-flight-customer", "test-flight", "icarus", "Test flight: user", "A user bot on a phone-sized viewport adds, opens and removes records and reloads.", ["Every step works at 390 by 844"], { dependsOn: ["sandbox-launch"] }),
        layer("test-flight-staff", "test-flight", "icarus", "Test flight: support", "A support bot checks the empty, offline and error states.", ["Each state shows plain words and a way forward"], { dependsOn: ["sandbox-launch"] }),
        layer("store-builds", "deploy", "icarus", "Store builds", "EAS build profiles for iOS and Android on the user's Expo account; submission waits for the user.", ["eas.json validates", "A preview build completes when the account is connected"], { dependsOn: ["test-flight-customer", "test-flight-staff"] }),
      ],
      checkpoint: { title: "Builds", question: "Test flights are green. Start store builds on your Expo account?" },
    },
  ],
  suggestedConnections: ["mod:launch", "adapter:supabase", "augment:supabase-mcp"],
  orchestration: [
    { key: "reviews", title: "Store reviews", agent: "metis", cadence: "weekly", description: "Reads new store reviews, groups them, and drafts replies for the user to approve.", connections: [] },
    { key: "crashes", title: "Crashes", agent: "icarus", cadence: "daily", description: "Checks crash and error reports from the latest release and reports new ones in plain words.", connections: ["mod:launch"] },
    { key: "release-notes", title: "Release notes", agent: "smith", cadence: "on-event", description: "Writes release notes from what changed before each store build.", connections: [] },
  ],
  designSystem: {
    name: "Pocket Dark",
    source: "kit",
    summary: "Dark, roomy and thumb-friendly; one accent for the main action.",
    tokens: {
      colors: [
        { name: "bg", value: "#0b0d0e", role: "background" },
        { name: "surface", value: "#15191b", role: "surface" },
        { name: "text", value: "#e8e2d0", role: "text" },
        { name: "primary", value: "#79c4b7", role: "primary" },
      ],
      spacing: [{ name: "touch", value: "44px" }],
      radii: [{ name: "card", value: "10px" }],
    },
    principles: ["One-handed first", "Works offline", "Ask for permissions at the moment they help"],
  },
  sourceRefs: ["https://docs.expo.dev/", "https://github.com/expo/expo/tree/main/templates/expo-template-blank-typescript"],
  tags: ["mobile", "phone", "app", "ios", "android", "iphone", "expo", "react native", "app store", "play store", "tracker", "habit"],
});
