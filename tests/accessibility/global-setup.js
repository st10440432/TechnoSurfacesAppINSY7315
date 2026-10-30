// Before the checks: one made-up quote, so the costing sheet, customer quotation and
// review screen have something to show. Made through the app's own API as the
// estimator, with a material line, a rate line and the quotation written from the
// costing. Its id is saved for the checks.
const { request } = require("@playwright/test");
const fs = require("fs");
const path = require("path");
const { accounts, signIn, tokenFrom } = require("./shared");

module.exports = async config => {
    const { baseURL } = config.projects[0].use;
    const api = await request.newContext({ baseURL, ignoreHTTPSErrors: true });
    await signIn(api, accounts.estimator);

    const token = tokenFrom(await (await api.get("/Home/Dashboard")).text());
    const headers = { RequestVerificationToken: token };
    async function call(method, url, data) {
        const response = await api.fetch(url, { method, data, headers });
        if (!response.ok()) throw new Error(`${method} ${url} answered ${response.status()}: ${await response.text()}`);
        return response.json();
    }

    const customer = await call("POST", "/api/customers", { name: `Accessibility test customer ${Date.now()}` });
    const withContact = await call("POST", `/api/customers/${customer.id}/contacts`, { fullName: "Test Contact" });
    const contactId = (withContact.contacts ? withContact.contacts[withContact.contacts.length - 1] : withContact).id;
    const quote = await call("POST", "/api/quotes", {
        reference: `A11Y-${Date.now()}`, customerId: customer.id, contactId, markupPercent: 35, site: "Test site", project: "Test project"
    });

    // The first material that has a price on the quote date.
    let added = false;
    for (const supplier of await call("GET", "/api/catalogue/suppliers")) {
        for (const line of await call("GET", `/api/catalogue/suppliers/${supplier.id}/product-lines`)) {
            for (const colour of await call("GET", `/api/catalogue/product-lines/${line.id}/colours`)) {
                const [size] = await call("GET", `/api/catalogue/colours/${colour.id}/sheet-sizes`);
                if (!size) continue;
                const response = await api.post(`/api/quotes/${quote.id}/lines`, {
                    headers, data: { type: "material", colourId: colour.id, sheetSizeId: size.id, quantity: 2 }
                });
                if (response.ok()) { added = true; break; }
            }
            if (added) break;
        }
        if (added) break;
    }
    if (!added) throw new Error("No material on the catalogue has a price, so the test quote has no material line.");

    const [rateItem] = await call("GET", "/api/catalogue/rate-items");
    await api.post(`/api/quotes/${quote.id}/lines`, { headers, data: { type: "rate", rateItemId: rateItem.id, quantity: 1, unitPrice: 100 } })
        .then(r => r.ok() ? r : api.post(`/api/quotes/${quote.id}/lines`, { headers, data: { type: "rate", rateItemId: rateItem.id, quantity: 1 } }));
    await call("POST", `/api/quotes/${quote.id}/quotation-lines/from-costing`, {});

    fs.mkdirSync(path.join(__dirname, "results"), { recursive: true });
    fs.writeFileSync(path.join(__dirname, "results", "quote.json"), JSON.stringify({ id: quote.id }));
    await api.dispose();
};
