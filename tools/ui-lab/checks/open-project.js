// Playwright check for the built page: open the Holodex project from the inventory and confirm the stage shows.
// Run: node tools/ui-lab/checks/open-project.js [path/to/index.html]
// Needs the playwright package resolvable from where it runs (a global install works).
const path = require('path');
const { pathToFileURL } = require('url');
const { chromium } = require('playwright');

const page = path.resolve(process.argv[2] || path.join(__dirname, '..', 'dist', 'index.html'));

(async () => {
  let failed = false;
  for (const reducedMotion of ['no-preference', 'reduce']) {
    const browser = await chromium.launch();
    const p = await browser.newPage({ viewport: { width: 1440, height: 900 }, reducedMotion });
    const errors = [];
    p.on('pageerror', e => errors.push(e.message));
    await p.goto(pathToFileURL(page).href);
    await p.waitForTimeout(1500);
    await p.click('[data-open-project=holodex]');
    await p.waitForTimeout(4000);
    const stageVisible = await p.locator('.rp-stage').isVisible();
    const inventoryHidden = await p.locator('#inventory').isHidden();
    const ok = stageVisible && inventoryHidden && errors.length === 0;
    console.log(JSON.stringify({ reducedMotion, stageVisible, inventoryHidden, pageErrors: errors, ok }));
    if (!ok) failed = true;
    await browser.close();
  }
  process.exit(failed ? 1 : 0);
})();
