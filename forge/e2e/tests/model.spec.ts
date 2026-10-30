import { expect, test } from "@playwright/test";
import { startForge, stubModelEnv, type ForgeServer } from "../support/forge.js";
import { startModelStub, type ModelStub } from "../support/model-stub.js";
import { checkScreen } from "../support/visual.js";

/**
 * Edge cases around the model, driven through the studio: no model key,
 * the model erroring, the model hanging past its timeout, an empty idea and
 * a nonsense idea. Each case runs against its own real 4GE server; the
 * model behind it is the scripted endpoint in support/model-stub.ts.
 */
test.describe("no model key", () => {
  let server: ForgeServer;
  test.beforeAll(async () => {
    server = await startForge({ name: "no-key", port: 17402, env: { NEBIUS_API_KEY: undefined, HIVEMIND_URL: undefined, HIVEMIND_TOKEN: undefined } });
  });
  test.afterAll(async () => server?.stop());

  test("the studio says plainly that no model is connected and how to connect one", async ({ page }) => {
    const models = await server.api.call("models.list", {});
    expect(models.route.provider).toBe("none");

    await page.goto(`${server.url}/imagistation`);
    await expect(page.getByTestId("model-status")).toHaveText(/No model connected/);
    const notice = page.getByTestId("model-notice");
    await expect(notice).toContainText("Prometheus has no model to think with.");
    await expect(notice).toContainText("Nebius Token Factory key");
    await checkScreen(page, "no-key-imagistation");

    // Starting anyway opens the session and says why Prometheus cannot answer, instead of hanging.
    await page.getByLabel("What do you want to build?").fill("A website for my tattoo shop");
    await page.getByTestId("start-idea").click();
    await expect(page).toHaveURL(/\/imagistation\/ses_/);
    await expect(page.getByTestId("thread")).toContainText(/No model is connected/, { timeout: 20_000 });
  });
});

test.describe("model failures", () => {
  let stub: ModelStub;
  let server: ForgeServer;
  test.beforeAll(async () => {
    stub = await startModelStub();
    server = await startForge({ name: "model-edge", port: 17403, env: { ...stubModelEnv(stub.baseUrl), FORGE_MODEL_TIMEOUT_MS: "3000" } });
  });
  test.afterAll(async () => {
    await server?.stop();
    await stub?.close();
  });
  test.beforeEach(() => {
    stub.mode = "ok";
  });

  test("an empty idea cannot be sent, and the button says why", async ({ page }) => {
    await page.goto(`${server.url}/imagistation`);
    await expect(page.getByTestId("model-status")).toHaveText(/Model on nebius/);
    const start = page.getByTestId("start-idea");
    await expect(start).toBeDisabled();
    await expect(page.getByText("Say what you want to build first.")).toBeVisible();
    await page.getByLabel("What do you want to build?").fill("    ");
    await expect(start).toBeDisabled();
    // The API refuses an empty Seed too, in plain words.
    const opened = await server.api.call("imagistation.sessions.create", { body: {} });
    const refused = await server.api.call("imagistation.seed", { params: { id: opened.session.id } }).catch((error: Error) => error);
    expect(refused).toBeInstanceOf(Error);
    expect((refused as Error).message).toBe("Say what you want to build first");
  });

  test("a nonsense idea gets a question back, not a Seed", async ({ page }) => {
    await page.goto(`${server.url}/imagistation`);
    await page.getByLabel("What do you want to build?").fill("asdf qwerty zxcv");
    await page.getByTestId("start-idea").click();
    await expect(page).toHaveURL(/\/imagistation\/ses_/);
    const thread = page.getByTestId("thread");
    await expect(thread).toContainText("I could not tell what you want to build", { timeout: 20_000 });
    await expect(page.getByText("Seed Scroll // not written")).toBeVisible();
    await checkScreen(page, "imagistation-nonsense");
  });

  test("a model error is reported in plain words and the person can try again", async ({ page }) => {
    await page.goto(`${server.url}/imagistation`);
    await page.getByLabel("What do you want to build?").fill("A website for my tattoo shop");
    stub.mode = "error";
    await page.getByTestId("start-idea").click();
    await expect(page).toHaveURL(/\/imagistation\/ses_/);
    await expect(page.getByTestId("thread")).toContainText(/Nebius Token Factory answered 500/, { timeout: 30_000 });
    // The provider comes back: the next message is answered.
    stub.mode = "ok";
    await page.getByLabel("Message Prometheus").fill("It is a tattoo shop called Ink and Iron.");
    await page.getByLabel("Message Prometheus").press("Enter");
    await expect(page.getByTestId("thread")).toContainText(/That is clear|Who are the people who will use Ink and Iron/, { timeout: 30_000 });
  });

  test("a model that hangs times out with a plain message instead of spinning forever", async ({ page }) => {
    await page.goto(`${server.url}/imagistation`);
    await page.getByLabel("What do you want to build?").fill("A website for my tattoo shop");
    stub.mode = "hang";
    const started = Date.now();
    await page.getByTestId("start-idea").click();
    // The button says it is working while Prometheus's first answer is awaited.
    await expect(page.getByTestId("start-idea")).toHaveText("Opening the session");
    await expect(page).toHaveURL(/\/imagistation\/ses_/, { timeout: 60_000 });
    await expect(page.getByTestId("thread")).toContainText(/timed out/i, { timeout: 60_000 });
    expect(Date.now() - started).toBeLessThan(60_000);
  });

  test("a refused key says to replace it", async () => {
    stub.mode = "refuse";
    const opened = await server.api.call("imagistation.sessions.create", { body: { idea: "A website for my tattoo shop" } });
    const messages = (await server.api.call("imagistation.sessions.get", { params: { id: opened.session.id } })).messages;
    const said = messages.map((message) => message.content).join("\n");
    expect(said).toMatch(/refused the key|No model is connected/);
  });
});
