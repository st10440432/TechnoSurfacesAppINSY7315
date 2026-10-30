// The demo accounts and signing in through the real sign-in form, with its
// antiforgery token, exactly as a browser does.
const password = process.env.DEMO_PASSWORD;
if (!password) throw new Error("Set DEMO_PASSWORD to the demo accounts' password.");

const accounts = {
    estimator: "lerato@technosurfaces.co.za",
    "managing director": "paul@technosurfaces.co.za"
};

function tokenFrom(html) {
    const match = /name="__RequestVerificationToken" type="hidden" value="([^"]+)"/.exec(html);
    if (!match) throw new Error("No antiforgery token on the page.");
    return match[1].replace(/&#x2B;/g, "+").replace(/&#x2F;/g, "/").replace(/&#x3D;/g, "=").replace(/&amp;/g, "&");
}

/** Signs an API request context in. */
async function signIn(api, email) {
    const form = await api.get("/Account/Login");
    const response = await api.post("/Account/Login", {
        form: { Email: email, Password: password, __RequestVerificationToken: tokenFrom(await form.text()) },
        maxRedirects: 0
    });
    if (response.status() !== 302) throw new Error(`Sign-in as ${email} answered ${response.status()}.`);
}

/** Signs a browser page in through the form. */
async function signInPage(page, email) {
    await page.goto("/Account/Login");
    await page.fill("#Email", email);
    await page.fill("#Password", password);
    await Promise.all([page.waitForURL(/\/Home\/Dashboard/), page.click("button[type=submit]")]);
}

module.exports = { accounts, tokenFrom, signIn, signInPage };
