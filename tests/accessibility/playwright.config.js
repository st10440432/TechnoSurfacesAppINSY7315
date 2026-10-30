// axe (WCAG 2.1 AA) checks of the main screens, at a phone and a desktop width.
// Run by the accessibility job in .github/workflows/ci.yml against the app it starts.
// Locally: start the app, set DEMO_PASSWORD to the demo accounts' password, then
// npm test. PW_CHANNEL=chrome uses the installed Chrome instead of Playwright's own.
const { defineConfig } = require("@playwright/test");

module.exports = defineConfig({
    testDir: ".",
    testMatch: "*.spec.js",
    globalSetup: require.resolve("./global-setup.js"),
    // One at a time: sign-in is rate limited to ten attempts a minute from one address.
    workers: 1,
    fullyParallel: false,
    retries: 0,
    timeout: 60_000,
    reporter: [["list"], ["json", { outputFile: "results/report.json" }]],
    use: {
        baseURL: process.env.BASE_URL || "https://localhost:7043",
        // The app runs on the .NET development certificate.
        ignoreHTTPSErrors: true,
        // Lets the test inject axe. The app's own security policy is unchanged.
        bypassCSP: true,
        channel: process.env.PW_CHANNEL || undefined
    },
    projects: [
        { name: "phone", use: { viewport: { width: 375, height: 812 } } },
        { name: "desktop", use: { viewport: { width: 1440, height: 900 } } }
    ]
});
