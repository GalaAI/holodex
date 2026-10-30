import type { BlueprintDependency, BlueprintInput, StagePlan } from "@4ge/contracts";
import { STARTER_STATE, defineBlueprint, layer, nameParameters } from "./define.js";

/**
 * Business records: a CRM (contacts and a sales pipeline) and an ERP
 * (products, stock, orders and invoices). Both start from the records-app
 * starter, which already has accounts with roles, contacts, deals in a
 * pipeline and an activity log; the ERP adds its operations layers.
 */
const RECORDS_DEPENDENCIES: BlueprintDependency[] = [
  { name: "hono", kind: "npm-package", scope: "runtime", versionRange: "^4.13.14", required: true, role: "HTTP API with role checks" },
  { name: "@hono/node-server", kind: "npm-package", scope: "runtime", versionRange: "^2.1.5", required: true, role: "Runs the API and serves the client" },
  { name: "react", kind: "npm-package", scope: "build", versionRange: "^19.3.1", required: true, role: "The interface" },
  { name: "node", kind: "runtime", scope: "runtime", versionRange: ">=24", required: true, role: "Runs the API, node:sqlite and node:test" },
  { name: "Supabase Postgres", kind: "service", scope: "runtime", required: false, role: "Hosted Postgres for teams and backups" },
];

const RECORDS_DESIGN: NonNullable<BlueprintInput["designSystem"]> = {
  name: "Ledger Dark",
  source: "kit",
  summary: "Dense, scannable tables and boards; numbers right-aligned; color only for status.",
  tokens: {
    colors: [
      { name: "bg", value: "#0b0d0e", role: "background" },
      { name: "surface", value: "#15191b", role: "surface" },
      { name: "text", value: "#e8e2d0", role: "text" },
      { name: "muted", value: "#8a877c", role: "muted" },
      { name: "primary", value: "#79c4b7", role: "primary" },
      { name: "won", value: "#8fcf8a", role: "success" },
      { name: "lost", value: "#e07a6b", role: "danger" },
    ],
    typography: [{ name: "body", family: "system-ui, sans-serif", size: "14px", weight: 400, lineHeight: "1.45" }],
  },
  principles: ["Find any record in two steps", "Color means status, nothing else", "Every change leaves a trail"],
};

const accessLayers = [
  layer(
    "roles-and-access",
    "backend",
    "hephaestus",
    "Roles and access",
    "Owner, manager, staff and viewer roles checked on every route; the owner assigns roles; every write lands in the activity log.",
    ["Each role is refused what it cannot do, in plain words", "Every write has an activity entry"],
    { dependsOn: ["records-model"], deliverables: ["server/app.ts", "server/auth.ts"] },
  ),
];

function deployStage(product: string): StagePlan {
  return {
    stage: "deploy",
    goal: `Run ${product} in the sandbox, test it as staff and as a customer, then deploy.`,
    layers: [
      layer("sandbox-launch", "deploy", "icarus", "Sandbox launch", "Build and run the service in the sandbox with a fresh database and a seeded owner account.", ["pnpm build and pnpm test pass", "GET /api/health answers ok"], { dependsOn: ["records-screens"] }),
      layer("test-flight-staff", "test-flight", "icarus", "Test flight: staff", "A staff bot adds records and moves work forward; a viewer bot is refused edits; a manager bot closes work out.", ["Each role can do exactly what it should"], { dependsOn: ["sandbox-launch"] }),
      layer("test-flight-customer", "test-flight", "icarus", "Test flight: customer", "A customer-facing step (inquiry, quote or order request) arrives as a new record in the right place.", ["The customer step creates exactly one record"], { dependsOn: ["sandbox-launch"] }),
      layer("deploy-service", "deploy", "icarus", "Deploy", "Deploy with a persistent database on the user's host through the Launch Mod, with daily backups.", ["Data survives a restart", "A backup restores"], { dependsOn: ["test-flight-staff", "test-flight-customer"] }),
    ],
    checkpoint: { title: "Go live", question: "Test flights are green. Deploy for your team now?" },
  };
}

export const CRM_BLUEPRINT = defineBlueprint({
  blueprintId: "blueprint:crm-pipeline",
  name: "CRM with sales pipeline",
  version: "1.0.0",
  state: STARTER_STATE,
  origin: "starter",
  kind: "crm",
  platforms: ["web"],
  description:
    "A CRM for a small team: contacts and companies, deals moving through a pipeline with stage totals, follow-up tasks, roles for owner, manager, staff and viewer, and an activity trail on every change.",
  starter: "records-app",
  parameters: [...nameParameters("Every customer and deal in one place."), { key: "pipelineStages", description: "Pipeline stages, comma separated", required: false, defaultValue: "lead, qualified, proposal, won, lost" }],
  dependencies: RECORDS_DEPENDENCIES,
  stages: [
    {
      stage: "architecture",
      goal: "Map how {{appName}} wins and keeps customers, then the records behind it.",
      layers: [
        layer("sales-process", "research", "metis", "Sales process", "How a lead becomes a customer today, the stages ({{pipelineStages}}), who touches each, and what gets dropped.", ["Each stage has an entry and exit rule"], { deliverables: ["docs/process.md"] }),
        layer("records-model", "data", "hephaestus", "Records model", "Contacts, companies, deals, tasks and the activity log, with stages as data.", ["Migrations run twice without error", "Deleting a contact keeps its deals"], { dependsOn: ["sales-process"], deliverables: ["server/db.ts"] }),
        ...accessLayers,
      ],
      checkpoint: { title: "Process and records", question: "Do these stages match how you sell?" },
    },
    {
      stage: "orchestration",
      goal: "Bring leads in and keep follow-ups from slipping.",
      layers: [
        layer("lead-intake", "integrations", "athena", "Lead intake", "Website forms and email inquiries become contacts and new deals; duplicates merge by email.", ["A form post creates one contact and one deal", "A repeat email updates the same contact"], { dependsOn: ["roles-and-access"] }),
        layer("follow-ups", "orchestration", "athena", "Follow-ups", "A task is due when a deal sits in a stage too long; the owner sees today's list.", ["A stale deal creates exactly one task"], { dependsOn: ["records-model"] }),
      ],
      checkpoint: { title: "Lead flow", question: "Where do your leads come from today?" },
    },
    {
      stage: "interface",
      goal: "Agree on the look, then build the board and the records screens.",
      layers: [
        layer("design-system", "design-system", "smith", "Design system", "Tokens and components for dense tables, boards, chips and forms.", ["Text meets WCAG AA contrast"]),
        layer("records-screens", "interface", "smith", "Pipeline and records", "Pipeline board with stage totals, contact search and detail, deal detail with activity, and the team and roles screen.", ["Find any contact in two steps", "Moving a deal updates totals"], { dependsOn: ["design-system", "roles-and-access"], deliverables: ["src/App.tsx"] }),
        layer("help-copy", "content", "metis", "Help and copy", "Labels, empty states and a short guide for each role.", ["No placeholder text is left"], { dependsOn: ["sales-process"] }),
      ],
      checkpoint: { title: "Screens", question: "Walk the pipeline: what is missing?" },
    },
    deployStage("the CRM"),
  ],
  suggestedConnections: ["mod:launch", "adapter:railway", "adapter:supabase", "adapter:docker", "augment:supabase-mcp"],
  orchestration: [
    { key: "follow-up-digest", title: "Follow-up digest", agent: "athena", cadence: "daily", description: "Sends each person their due follow-ups and stale deals each morning.", connections: [] },
    { key: "pipeline-report", title: "Pipeline report", agent: "metis", cadence: "weekly", description: "Summarizes new leads, stage movement, won and lost value, and where deals stall.", connections: [] },
    { key: "data-hygiene", title: "Data hygiene", agent: "metis", cadence: "weekly", description: "Finds duplicate contacts and deals with no next step and proposes fixes.", connections: [] },
    { key: "backups", title: "Backups", agent: "icarus", cadence: "daily", description: "Backs up the database and checks a restore each week.", connections: ["mod:launch"] },
  ],
  designSystem: RECORDS_DESIGN,
  sourceRefs: ["GalaAI/ark packages/atlas-core/src/build-registry.ts", "https://cheatsheetseries.owasp.org/cheatsheets/Authorization_Cheat_Sheet.html"],
  tags: ["crm", "customers", "clients", "contacts", "leads", "sales", "pipeline", "deals", "follow up", "roles", "records", "team"],
});

export const ERP_BLUEPRINT = defineBlueprint({
  blueprintId: "blueprint:erp-operations",
  name: "ERP for small operations",
  version: "1.0.0",
  state: STARTER_STATE,
  origin: "starter",
  kind: "erp",
  platforms: ["web"],
  description:
    "Operations software for a small business: products and stock, purchase and sales orders, invoices and payments recorded, customers and suppliers, roles for owner, manager, staff and viewer, and an activity trail on every change.",
  starter: "records-app",
  parameters: [...nameParameters("Stock, orders and invoices in one place."), { key: "currency", description: "Three-letter currency code", required: false, defaultValue: "USD" }],
  dependencies: RECORDS_DEPENDENCIES,
  stages: [
    {
      stage: "architecture",
      goal: "Map how goods and money move through {{appName}}, then the records behind it.",
      layers: [
        layer("operations-map", "research", "metis", "Operations map", "How stock arrives, is sold and is paid for today; the documents involved; who approves what.", ["Each flow names its documents and approver"], { deliverables: ["docs/operations.md"] }),
        layer("records-model", "data", "hephaestus", "Records model", "Customers, suppliers, products, stock movements, orders, invoices and payments in {{currency}}, with stock computed from movements.", ["Stock always equals the sum of its movements", "Money is stored in minor units"], { dependsOn: ["operations-map"], deliverables: ["server/db.ts"] }),
        ...accessLayers,
        layer("orders-and-invoices", "backend", "hephaestus", "Orders and invoices", "Sales orders reserve stock, invoices come from orders, payments close invoices; purchase orders receive stock.", ["Selling more than in stock is refused in plain words", "An invoice cannot be paid twice"], { dependsOn: ["roles-and-access"] }),
      ],
      checkpoint: { title: "Operations and records", question: "Does this match how goods and money move in your business?" },
    },
    {
      stage: "orchestration",
      goal: "Connect stock, orders and money so nothing is typed twice.",
      layers: [
        layer("stock-alerts", "orchestration", "athena", "Stock alerts", "A reorder task when stock falls under its minimum, with a draft purchase order.", ["Falling under the minimum creates exactly one task"], { dependsOn: ["orders-and-invoices"] }),
        layer("accounting-export", "integrations", "athena", "Accounting export", "Invoices and payments export as CSV for the user's accounting tool.", ["The export opens in a spreadsheet with correct totals"], { dependsOn: ["orders-and-invoices"] }),
      ],
      checkpoint: { title: "Flows", question: "Which accounting tool should invoices go to?" },
    },
    {
      stage: "interface",
      goal: "Agree on the look, then build the operations screens.",
      layers: [
        layer("design-system", "design-system", "smith", "Design system", "Tokens and components for dense tables, forms, totals and status chips.", ["Text meets WCAG AA contrast", "Numbers are right-aligned"]),
        layer("records-screens", "interface", "smith", "Operations screens", "Products and stock, orders, invoices, customers and suppliers, and the team and roles screen.", ["Create an order to invoice to paid in under a minute"], { dependsOn: ["design-system", "orders-and-invoices"], deliverables: ["src/App.tsx"] }),
        layer("help-copy", "content", "metis", "Help and copy", "Labels, empty states and a short guide for each role.", ["No placeholder text is left"], { dependsOn: ["operations-map"] }),
      ],
      checkpoint: { title: "Screens", question: "Walk an order from start to paid: what is missing?" },
    },
    deployStage("the ERP"),
  ],
  suggestedConnections: ["mod:launch", "adapter:railway", "adapter:supabase", "adapter:docker", "augment:supabase-mcp"],
  orchestration: [
    { key: "reorder", title: "Reorder watch", agent: "athena", cadence: "daily", description: "Drafts purchase orders for low stock for a manager to approve.", connections: [] },
    { key: "receivables", title: "Receivables", agent: "athena", cadence: "weekly", description: "Lists overdue invoices and drafts polite reminders for the owner to send.", connections: [] },
    { key: "month-end", title: "Month end", agent: "metis", cadence: "monthly", description: "Prepares the sales, stock value and receivables summary and the accounting export.", connections: [] },
    { key: "backups", title: "Backups", agent: "icarus", cadence: "daily", description: "Backs up the database and checks a restore each week.", connections: ["mod:launch"] },
  ],
  designSystem: RECORDS_DESIGN,
  sourceRefs: ["GalaAI/ark packages/atlas-core/src/build-registry.ts", "https://cheatsheetseries.owasp.org/cheatsheets/Authorization_Cheat_Sheet.html"],
  tags: ["erp", "operations", "inventory", "stock", "warehouse", "orders", "invoices", "invoicing", "purchasing", "suppliers", "products", "accounting", "back office"],
});
