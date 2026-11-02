// axe (WCAG 2.1 AA) on the main screens, signed in as each role. Any violation fails
// the run; every screen's result is saved under results/ for the CI artifact.
const { test, expect } = require("@playwright/test");
const AxeBuilder = require("@axe-core/playwright").default;
const fs = require("fs");
const path = require("path");
const { accounts, signInPage } = require("./shared");

const tags = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

// The test quote is made by global-setup.js, so its id is read when a check runs.
const quoteId = () => JSON.parse(fs.readFileSync(path.join(__dirname, "results", "quote.json"), "utf8")).id;

const screens = {
    "dashboard": () => "/Home/Dashboard",
    "quote list": () => "/Quotes/Index",
    "costing sheet": () => `/Quotes/Costing/${quoteId()}`,
    "customer quotation": () => `/Quotes/Quotation/${quoteId()}`,
    "review": () => `/Quotes/Review/${quoteId()}`,
    "customers": () => "/Customers/Index",
    "catalogue": () => "/Catalogue/Index",
    "users": () => "/Admin/Users"
};

async function check(page, testInfo, url) {
    const response = await page.goto(url, { waitUntil: "networkidle" });
    expect(response.status(), `${url} did not open`).toBe(200);

    const result = await new AxeBuilder({ page }).withTags(tags).analyze();

    const folder = path.join(__dirname, "results", testInfo.project.name);
    fs.mkdirSync(folder, { recursive: true });
    const file = testInfo.titlePath.slice(1).join(" ").replace(/[^a-z0-9]+/gi, "-").toLowerCase();
    fs.writeFileSync(path.join(folder, `${file}.json`),
        JSON.stringify({ url, violations: result.violations, passes: result.passes.length }, null, 2));

    const summary = result.violations
        .map(v => `${v.id}: ${v.help} (${v.nodes.length}) ${v.nodes.slice(0, 3).map(n => n.target.join(" ")).join(", ")}`)
        .join("\n");
    expect(result.violations, `${url} has accessibility violations:\n${summary}`).toEqual([]);
}

test("sign in", async ({ page }, testInfo) => {
    await check(page, testInfo, "/Account/Login");
});

for (const [role, email] of Object.entries(accounts)) {
    test.describe(role, () => {
        test.describe.configure({ mode: "serial" });
        let context;
        let page;

        // Signed in once per role, so the run stays inside the sign-in rate limit.
        test.beforeAll(async ({ browser }, testInfo) => {
            const { baseURL, ignoreHTTPSErrors, bypassCSP, viewport } = testInfo.project.use;
            context = await browser.newContext({ baseURL, ignoreHTTPSErrors, bypassCSP, viewport });
            page = await context.newPage();
            await signInPage(page, email);
        });

        test.afterAll(async () => {
            if (context) await context.close();
        });

        for (const [name, url] of Object.entries(screens)) {
            test(name, async ({}, testInfo) => {
                await check(page, testInfo, url());
            });
        }
    });
}
