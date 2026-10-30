import type { ComponentCandidate } from "@4ge/contracts";

/**
 * Component candidates for the inventory picker, each with its source.
 *
 * - kit: the components @4ge/kit exports (src/index.ts). Descriptions are
 *   condensed from each component's prompts/<Name>.prompt.md.
 * - 21st-snapshot: real entries from the 21st.dev registry catalog, found
 *   with the 21st registry search (the 21st-registry skill's catalog, via
 *   the 21st MCP `search` tool) on 2026-10-30 for the website categories a
 *   build needs first. Each keeps its author, page URL, preview image and
 *   the shadcn add line the registry publishes. The add line reads the
 *   user's own 21st key from $API_KEY_21ST; 4GE never stores one.
 *
 * The server adds 21st-live results on top when the 21st CLI is installed.
 */

type KitEntry = Pick<ComponentCandidate, "name" | "category" | "description"> & { exportName: string };

const KIT_ENTRIES: KitEntry[] = [
  { exportName: "Button", name: "Button", category: "input", description: "Uppercase button; one primary per region, danger in rust, accent pill for the zone action." },
  { exportName: "IconButton", name: "Icon button", category: "input", description: "Square icon-only button with an accessible label." },
  { exportName: "ActionPill", name: "Action pill", category: "input", description: "Selectable action chip that names an action and carries its risk tier." },
  { exportName: "TextLink", name: "Text link", category: "navigation", description: "Mono route pointer that moves the person to another page." },
  { exportName: "Eyebrow", name: "Eyebrow", category: "content", description: "Uppercase mono section label, // separated." },
  { exportName: "SpineLabel", name: "Spine label", category: "display", description: "Label riding a card's left edge, bottom to top, in the card's status tone." },
  { exportName: "TextField", name: "Text field", category: "input", description: "Single-line entry with label above and hint below; 44px touch height." },
  { exportName: "SelectField", name: "Select field", category: "input", description: "Native select in the text field box." },
  { exportName: "CheckboxField", name: "Checkbox field", category: "input", description: "Labelled checkbox with hint." },
  { exportName: "FileField", name: "File field", category: "input", description: "File picker in the text field box." },
  { exportName: "TextArea", name: "Text area", category: "input", description: "Multi-line entry with the same label, box and hint as the text field." },
  { exportName: "FluidSelect", name: "Fluid select", category: "input", description: "The single-choice dropdown with a spring reveal and a gliding highlight." },
  { exportName: "Led", name: "Status light", category: "feedback", description: "Status LED with a mono label: live, warn, error, ok." },
  { exportName: "Badge", name: "Badge", category: "feedback", description: "Lifecycle badge: unstated, validated, proven, blocked." },
  { exportName: "CadenceMark", name: "Thinking mark", category: "feedback", description: "Small mark that shows an agent thinking, done or stopped." },
  { exportName: "Skeleton", name: "Skeleton", category: "feedback", description: "Loading placeholder block in the skeleton tone." },
  { exportName: "Panel", name: "Panel", category: "layout", description: "Matte dark container that casts a shadow onto the page." },
  { exportName: "Plate", name: "Plate", category: "layout", description: "The one light cream surface per view, for headers and hero artifacts." },
  { exportName: "FocalCard", name: "Focal card", category: "display", description: "The one focal object: eyebrow, title, what happened, facts and the actions that resolve it." },
  { exportName: "Metric", name: "Metric", category: "display", description: "Label, current value and an optional note." },
  { exportName: "AccentTile", name: "Accent tile", category: "display", description: "Warm accent tile with a mono label and a display value." },
  { exportName: "StageCanvas", name: "Stage canvas", category: "media", description: "Pannable, zoomable board that hosts previews." },
  { exportName: "ThinkingBlock", name: "Thinking block", category: "feedback", description: "Agent reasoning block above the answer it produced." },
  { exportName: "GateCard", name: "Gate card", category: "feedback", description: "Approval card: what is asked, changed files, checks, approve or reject." },
  { exportName: "IntakeComposer", name: "Intake composer", category: "input", description: "Note and file form inside a panel." },
  { exportName: "WorkspaceFrame", name: "Workspace frame", category: "layout", description: "One action canvas with one contextual inspector." },
  { exportName: "ActivityTrail", name: "Activity trail", category: "display", description: "Compact trail of recent receipts." },
];

export function kitCandidates(): ComponentCandidate[] {
  return KIT_ENTRIES.map((entry) => ({
    key: `kit:${entry.exportName}`,
    name: entry.name,
    category: entry.category,
    description: entry.description,
    source: "kit",
    sourceRef: entry.exportName,
    via: "kit",
  }));
}

export const REGISTRY_SNAPSHOT_AT = "2026-10-30T00:00:00.000Z";

type SnapshotEntry = {
  demoId: number;
  author: string;
  slug: string;
  name: string;
  category: ComponentCandidate["category"];
  description: string;
  previewUrl: string;
};

const SNAPSHOT: SnapshotEntry[] = [
  { demoId: 38089, author: "redrob-labs", slug: "hero", name: "Hero Section", category: "layout", description: "Page-top hero with a statement heading, supporting lede, one primary action and a secondary link, optionally with a media figure or background film.", previewUrl: "https://cdn.21st.dev/user_registry_redrob-labs_1791304190596/hero/default/preview.1791973667459.png" },
  { demoId: 8737, author: "ravikatiyar162", slug: "hero-section-9", name: "Hero Section", category: "layout", description: "Two-column hero with animated text, action buttons, social proof numbers and an image collage.", previewUrl: "https://cdn.21st.dev/ravikatiyar162/hero-section-9/default/preview.1760172786727.png" },
  { demoId: 29522, author: "uiable", slug: "block-hero", name: "Marketing Hero Section", category: "layout", description: "Full-width hero with a stats pill, animated headline, call-to-action buttons and a cursor-following spotlight.", previewUrl: "https://cdn.21st.dev/user_registry_uiable_1783486969177/block-hero/default/preview.1790091001503.webp" },
  { demoId: 18258, author: "karthikmudunuri", slug: "header-02", name: "Header Navbar", category: "navigation", description: "Responsive site header with navigation, animated logo, optional banner slot and an animated mobile menu.", previewUrl: "https://cdn.21st.dev/eldoraui/header-02/default/preview.1783571038832.png" },
  { demoId: 32097, author: "mohitsapat", slug: "navbar", name: "NavBar", category: "navigation", description: "Responsive navigation bar with a language menu, floating variant, animated link highlight and mobile menu.", previewUrl: "https://cdn.21st.dev/babelize/navbar/non-sticky-navbar/preview.1790374582925-93e7694d-eda6-439f-a03d-afa64d93bed9.png" },
  { demoId: 1024, author: "manuarora700", slug: "navbar-menu", name: "Navbar Menu", category: "navigation", description: "Navbar menu that animates its children on hover into a large menu.", previewUrl: "https://cdn.21st.dev/user_aceternity/navbar-menu/1024/preview.png" },
  { demoId: 29211, author: "olewandowski1", slug: "booking-2", name: "Appointment Booking Split", category: "booking", description: "Two-column appointment request form with service reassurance points beside the booking form.", previewUrl: "https://cdn.21st.dev/7ovr/booking-2/default/preview.1790051732678-c6a5a283-c945-4c86-8018-47c239f5ec45.png" },
  { demoId: 25128, author: "cnippet-dev", slug: "v-calendar-8", name: "Appointment Booking Calendar", category: "booking", description: "Booking calendar that greys out weekends, past dates and fully booked days and shows the chosen slot.", previewUrl: "https://cdn.21st.dev/user_36Tbt0v8JdD4jEycmBFhR8tojnR/v-calendar-8/default/preview.1787760081693.webp" },
  { demoId: 38035, author: "kedhareswer", slug: "open-slot-booking", name: "Meeting Booking Calendar", category: "booking", description: "Book-a-call section with host card, call lengths, a week of open slots and a confirm form.", previewUrl: "https://cdn.21st.dev/kedhareswer/open-slot-booking/default/preview.1791954021018-34f09e50-c1ef-430e-ac08-3a79cf3d91a7.png" },
  { demoId: 26909, author: "ln-dev7", slug: "gallery-02", name: "Gallery Grid with Captions", category: "media", description: "Two-column responsive image gallery with a title and caption under each tile.", previewUrl: "https://cdn.21st.dev/ln-dev7/gallery-02/default/preview.1788991653220-35c1c731-dfe2-4e25-ba61-ddaf09c17cb3.png" },
  { demoId: 29523, author: "uiable", slug: "block-portfolio-9", name: "Portfolio Showcase Grid", category: "media", description: "Portfolio section with a heading, call to action and an asymmetric grid of project cards.", previewUrl: "https://cdn.21st.dev/uiable/block-portfolio-9/default/preview.1790086682734-33f0427f-21b1-4e8b-a623-f01c78afd196.png" },
  { demoId: 7517, author: "isaiahbjork", slug: "portfolio-gallery", name: "Portfolio Gallery", category: "media", description: "Overlapping 3D portfolio layout on desktop and a horizontal marquee on mobile.", previewUrl: "https://cdn.21st.dev/isaiahbjork/portfolio-gallery/default/preview.1757974061317.png" },
  { demoId: 7267, author: "efferd", slug: "testimonials-section", name: "Testimonials Section", category: "content", description: "Testimonial grid with customer photos and quotes.", previewUrl: "https://cdn.21st.dev/sshahaider/testimonials-section/default/preview.1757677151700.png" },
  { demoId: 2568, author: "maxim.bort.devel", slug: "circular-testimonials", name: "Circular Testimonials", category: "content", description: "Animated testimonial section that rotates customer feedback.", previewUrl: "https://cdn.21st.dev/maxim.bort.devel/circular-testimonials/default/preview.1749048275501.png" },
  { demoId: 40206, author: "my", slug: "pricing-table", name: "Pricing Table", category: "commerce", description: "Pricing section with a monthly or yearly toggle, rolling prices and plan cards with a highlighted plan.", previewUrl: "https://cdn.21st.dev/my/pricing-table/default/preview.1792526138790-f202e0f2-2873-496a-bc96-0555fa642a72.png" },
  { demoId: 6247, author: "uilayout.contact", slug: "pricing-section", name: "Pricing Section", category: "commerce", description: "Pricing tiers with feature comparison.", previewUrl: "https://cdn.21st.dev/user_2zAsvSHo1IeJGKtSjJ3uR1N1Cd7/pricing-section/default/preview.1785944436174.webp" },
  { demoId: 33904, author: "balick", slug: "footer-01", name: "Footer With Newsletter", category: "navigation", description: "Multi-column site footer with a newsletter signup, status light, social links and a large wordmark.", previewUrl: "https://cdn.21st.dev/balick/footer-01/default/preview.1790754388831-9bf30249-3386-4757-8cbc-b43bc4491afb.png" },
  { demoId: 29773, author: "scrollxui", slug: "footer-with-suite", name: "Footer with Suite", category: "navigation", description: "Split footer with navigation and social links on one side, contact details on the other.", previewUrl: "https://cdn.21st.dev/scrollxui/footer-with-suite/default/preview.1790118388011-7721a04e-5e08-4fc0-b2f4-57bb758434f0.png" },
];

export function registryRef(author: string, slug: string): string {
  return `${author}/${slug}`;
}

/** The shadcn add line the 21st registry publishes for a component (the key comes from the user's env). */
export function registryInstallCommand(author: string, slug: string): string {
  return `npx shadcn@latest add "https://21st.dev/r/${author}/${slug}?api_key=$API_KEY_21ST"`;
}

export function registrySnapshotCandidates(): ComponentCandidate[] {
  return SNAPSHOT.map((entry) => ({
    key: `21st:${registryRef(entry.author, entry.slug)}`,
    name: entry.name,
    category: entry.category,
    description: entry.description,
    source: "21st",
    sourceRef: registryRef(entry.author, entry.slug),
    sourceUrl: `https://21st.dev/@${entry.author}/components/${entry.slug}`,
    author: entry.author,
    previewUrl: entry.previewUrl,
    installCommand: registryInstallCommand(entry.author, entry.slug),
    via: "21st-snapshot",
    fetchedAt: REGISTRY_SNAPSHOT_AT,
  }));
}

/** Words that map a free-text need onto a category, for filtering. */
const CATEGORY_WORDS: Record<ComponentCandidate["category"], string[]> = {
  layout: ["hero", "section", "layout", "panel", "landing", "header"],
  navigation: ["nav", "navbar", "menu", "header", "footer", "link"],
  input: ["form", "input", "button", "field", "select", "contact"],
  display: ["card", "metric", "stat", "badge"],
  feedback: ["status", "loading", "toast", "alert"],
  media: ["gallery", "image", "portfolio", "video", "photo"],
  commerce: ["pricing", "price", "shop", "cart", "checkout", "plan"],
  booking: ["booking", "appointment", "calendar", "schedule", "reserve"],
  content: ["testimonial", "review", "faq", "blog", "text", "about"],
  other: [],
};

/** Rank candidates for a query: name and description words, then category words. Empty query keeps order. */
export function filterCandidates(candidates: ComponentCandidate[], query: string | undefined): ComponentCandidate[] {
  const words = (query ?? "")
    .toLowerCase()
    .split(/[^a-z0-9]+/)
    .filter((word) => word.length > 1);
  if (words.length === 0) return candidates;
  const scored = candidates.map((candidate) => {
    const text = `${candidate.name} ${candidate.description} ${candidate.sourceRef}`.toLowerCase();
    let score = 0;
    for (const word of words) {
      if (text.includes(word)) score += 3;
      if (CATEGORY_WORDS[candidate.category].some((hint) => hint.startsWith(word) || word.startsWith(hint))) score += 1;
    }
    return { candidate, score };
  });
  return scored
    .filter((entry) => entry.score > 0)
    .sort((a, b) => b.score - a.score)
    .map((entry) => entry.candidate);
}

/** Guess a category for a live registry result from its name and description. */
export function guessCategory(text: string): ComponentCandidate["category"] {
  const lower = text.toLowerCase();
  let best: ComponentCandidate["category"] = "other";
  let bestScore = 0;
  for (const [category, hints] of Object.entries(CATEGORY_WORDS) as Array<[ComponentCandidate["category"], string[]]>) {
    const score = hints.filter((hint) => lower.includes(hint)).length;
    if (score > bestScore) {
      best = category;
      bestScore = score;
    }
  }
  return best;
}
