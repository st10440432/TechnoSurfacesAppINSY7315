/* ==========================================================================
   Techno Surfaces: new quote

   Narrows the contact list to the chosen customer's contacts, so a quote cannot
   be addressed to someone at another company. Without JavaScript every contact
   is listed with its company name, and the server still checks the pair.
   ========================================================================== */

(function () {
    "use strict";

    var customer = document.querySelector("[data-customer-select]");
    var contact = document.querySelector("[data-contact-select]");
    if (!customer || !contact) return;

    // Keep every contact so the list can be rebuilt each time the customer changes.
    var all = Array.prototype.slice.call(contact.querySelectorAll("option[data-customer]")).map(function (o) {
        return { value: o.value, customer: o.dataset.customer, name: o.dataset.name };
    });
    var placeholder = contact.querySelector('option[value=""]');

    function show() {
        var chosen = contact.value;
        var matches = all.filter(function (c) { return c.customer === customer.value; });

        contact.replaceChildren(placeholder);
        matches.forEach(function (c) {
            var option = new Option(c.name, c.value);
            option.selected = c.value === chosen;
            contact.appendChild(option);
        });

        if (!customer.value) {
            placeholder.textContent = "Choose a customer first";
            contact.disabled = true;
        } else if (matches.length === 0) {
            placeholder.textContent = "This customer has no active contacts";
            contact.disabled = true;
        } else {
            placeholder.textContent = "Choose a contact";
            contact.disabled = false;
            // One contact is the only sensible choice, so it is chosen.
            if (matches.length === 1) contact.value = matches[0].value;
        }
    }

    customer.addEventListener("change", show);
    show();
})();
