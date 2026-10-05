/* ==========================================================================
   Techno Surfaces: the costing sheet

   - The material cascade: supplier, product line, colour, sheet size (US-01).
     Each step loads the next from /api/catalogue.
   - The price of the chosen sheet, where it came from and the sheet's size,
     shown before the line is added (NFR-01, US-02). A price that cannot be found
     is shown as a blocking, explained error and the line cannot be added (US-03).
   - Every change to a line, the markup or the transport is saved through the
     costing API as soon as the field is left. The totals update from the answer
     and are read out to a screen reader, and the line tables are refreshed from
     the server because one change can move other lines (US-05).

   Uses TS from app.js for the API, messages, busy buttons and confirmation.
   ========================================================================== */

(function () {
    "use strict";

    var root = document.getElementById("costing");
    if (!root || !window.TS) return;

    var TS = window.TS;
    var F = TS.format;
    var api = root.dataset.api;
    var catalogue = root.dataset.catalogue;

    // ------------------------------------------------------------ small helpers

    function el(tag, className, text) {
        var node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined && text !== null) node.textContent = text;
        return node;
    }

    function icon(name, extraClass) {
        var svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
        svg.setAttribute("class", "ico" + (extraClass ? " " + extraClass : ""));
        svg.setAttribute("aria-hidden", "true");
        var use = document.createElementNS("http://www.w3.org/2000/svg", "use");
        use.setAttribute("href", "#" + name);
        svg.appendChild(use);
        return svg;
    }

    function withQuery(url, params) {
        var query = Object.keys(params).map(function (k) {
            return encodeURIComponent(k) + "=" + encodeURIComponent(params[k]);
        }).join("&");
        return url + (url.indexOf("?") === -1 ? "?" : "&") + query;
    }

    function announce(id, message) {
        var region = document.getElementById(id);
        if (!region) return;
        // Cleared first so the same message is read again when it repeats.
        region.textContent = "";
        setTimeout(function () { region.textContent = message; }, 50);
    }

    // ------------------------------------------------------------ save state

    var saveState = document.getElementById("save-state");
    var pending = 0;

    function saving(change) {
        pending = Math.max(0, pending + change);
        if (!saveState) return;
        var text = saveState.querySelector("[data-save-text]");
        if (pending > 0) {
            saveState.dataset.state = "saving";
            text.textContent = "Saving";
        } else if (saveState.dataset.state !== "failed") {
            saveState.dataset.state = "saved";
            text.textContent = "All changes saved";
        }
    }

    function saveFailed() {
        if (!saveState) return;
        saveState.dataset.state = "failed";
        saveState.querySelector("[data-save-text]").textContent = "Last change not saved";
    }

    function saveSucceeded() {
        if (saveState && saveState.dataset.state === "failed") saveState.dataset.state = "saved";
    }

    window.addEventListener("beforeunload", function (e) {
        if (pending > 0) {
            e.preventDefault();
            e.returnValue = "";
        }
    });

    // ------------------------------------------------------------ totals

    var totalsPanel = document.getElementById("totals");

    function totalsBusy(on) {
        if (!totalsPanel) return;
        totalsPanel.classList.toggle("is-updating", on);
        if (on) totalsPanel.setAttribute("aria-busy", "true");
        else totalsPanel.removeAttribute("aria-busy");
    }

    function showTotals(t) {
        if (!t || !totalsPanel) return;
        var values = Object.assign({}, t, {
            randPerM2: t.totalAreaM2 > 0 ? t.totalExVat / t.totalAreaM2 : null
        });
        totalsPanel.querySelectorAll("[data-total]").forEach(function (node) {
            var value = values[node.dataset.total];
            if (value === undefined) return;
            switch (node.dataset.format) {
                case "rate": node.textContent = F.percent(value * 100); break;
                case "area": node.textContent = F.area(value); break;
                case "quantity": node.textContent = F.quantity(value); break;
                default: node.textContent = value === null ? "Not yet" : F.rand(value);
            }
        });
        totalsBusy(false);
        announce("totals-status",
            "Totals updated. Total excluding VAT " + F.rand(t.totalExVat) +
            ". Total including VAT " + F.rand(t.totalIncVat) +
            ". " + F.area(t.totalAreaM2) + " of material.");
    }

    function reloadTotals() {
        return TS.api("GET", api + "totals").then(function (result) {
            if (result.ok) showTotals(result.data);
            else totalsBusy(false);
        });
    }

    // ------------------------------------------------------------ refreshing the tables

    var refreshUrl = root.dataset.linesUrl;

    /**
     * Swaps in the server's latest line tables, quotation check and next step.
     * Keeps the user's place: the field that had focus has it again afterwards,
     * with anything already typed into it.
     */
    function refresh() {
        return fetch(refreshUrl, { credentials: "same-origin", headers: { "Accept": "text/html" } })
            .then(function (response) {
                if (!response.ok) throw new Error(String(response.status));
                return response.text();
            })
            .then(function (html) {
                var fresh = new DOMParser().parseFromString(html, "text/html");
                var active = document.activeElement;
                var activeId = active && active.id;
                var typed = active && active.matches && active.matches("input[data-saved]") && active.value !== active.dataset.saved
                    ? active.value : null;

                ["material-lines", "rate-lines", "costing-side"].forEach(function (id) {
                    var incoming = fresh.getElementById(id);
                    var current = document.getElementById(id);
                    if (incoming && current) current.replaceWith(document.importNode(incoming, true));
                });

                if (activeId) {
                    var again = document.getElementById(activeId);
                    if (again && again !== active) {
                        if (typed !== null) again.value = typed;
                        again.focus();
                    }
                }
            })
            .catch(function () {
                TS.toast("error", "The sheet could not be refreshed",
                    "Your change was saved. Reload the page to see the latest figures.");
            });
    }

    // ------------------------------------------------------------ field errors on the sheet

    function fieldError(input, message) {
        clearFieldError(input);
        var id = input.id + "-error";
        var span = el("span", "field-error", message);
        span.id = id;
        span.setAttribute("data-sheet-error", "");
        (input.closest(".input-group") || input).insertAdjacentElement("afterend", span);
        input.setAttribute("aria-invalid", "true");
        var described = (input.getAttribute("aria-describedby") || "").split(" ").filter(Boolean);
        if (described.indexOf(id) === -1) described.push(id);
        input.setAttribute("aria-describedby", described.join(" "));
    }

    function clearFieldError(input) {
        var id = input.id + "-error";
        var existing = document.getElementById(id);
        if (existing) existing.remove();
        input.removeAttribute("aria-invalid");
        var rest = (input.getAttribute("aria-describedby") || "").split(" ").filter(function (x) { return x && x !== id; });
        if (rest.length) input.setAttribute("aria-describedby", rest.join(" "));
        else input.removeAttribute("aria-describedby");
    }

    /** Checks a number field before anything is sent. Returns the message, or null when it is fine. */
    function problemWith(input) {
        var raw = input.value.trim();
        if (raw === "" || isNaN(Number(raw))) return "Enter a number.";
        var value = Number(raw);
        var min = input.getAttribute("min");
        var max = input.getAttribute("max");
        if (min !== null && value < Number(min)) {
            return Number(min) > 0 ? "Enter an amount greater than zero." : "Enter 0 or more.";
        }
        if (max !== null && value > Number(max)) return "Enter " + F.number(Number(max), Number(max) % 1 ? 2 : 0) + " or less.";
        return null;
    }

    // ------------------------------------------------------------ saving a field

    function describe(result, fallback) {
        return {
            title: result.problem && result.problem.title ? result.problem.title : fallback,
            detail: TS.describeProblem(result.problem)
        };
    }

    function saveLineField(input) {
        if (input.value === input.dataset.saved || input.dataset.inflight === input.value) return;

        var problem = problemWith(input);
        if (problem) { fieldError(input, problem); return; }
        clearFieldError(input);

        var row = input.closest("[data-line-row]");
        var body = {};
        body[input.dataset.field] = Number(input.value);
        input.dataset.inflight = input.value;

        saving(+1);
        totalsBusy(true);
        if (row) row.classList.add("is-saving");

        TS.api("PUT", api + "lines/" + input.dataset.line, body).then(function (result) {
            delete input.dataset.inflight;
            if (result.ok) {
                input.dataset.saved = input.value;
                saveSucceeded();
                showTotals(result.data.totals);
                return refresh().then(function () { saving(-1); });
            }

            saving(-1);
            saveFailed();
            totalsBusy(false);
            if (row) row.classList.remove("is-saving");
            var said = describe(result, "The change was not saved");
            if (result.status === 400 || result.status === 422) fieldError(input, said.detail);
            TS.toast("error", said.title, said.detail + (result.status === 409 ? " Reload the page to see the quote as it is now." : ""));
        });
    }

    function saveCostingField(input) {
        if (input.value === input.dataset.saved || input.dataset.inflight === input.value) return;

        var problem = problemWith(input);
        if (problem) { fieldError(input, problem); return; }
        clearFieldError(input);

        var body = {};
        body[input.dataset.costingField] = Number(input.value);
        input.dataset.inflight = input.value;

        saving(+1);
        totalsBusy(true);

        TS.api("PUT", api + "costing", body).then(function (result) {
            delete input.dataset.inflight;
            if (result.ok) {
                input.dataset.saved = input.value;
                saveSucceeded();
                showTotals(result.data);
                return refresh().then(function () { saving(-1); });
            }

            saving(-1);
            saveFailed();
            totalsBusy(false);
            var said = describe(result, "The change was not saved");
            if (result.status === 400) fieldError(input, said.detail);
            TS.toast("error", said.title, said.detail);
        });
    }

    root.addEventListener("change", function (e) {
        var lineInput = e.target.closest("input[data-line]");
        if (lineInput) { saveLineField(lineInput); return; }
        var costingInput = e.target.closest("input[data-costing-field]");
        if (costingInput) saveCostingField(costingInput);
    });

    // Enter saves at once; Escape puts back the saved value.
    root.addEventListener("keydown", function (e) {
        var input = e.target.closest("input[data-line], input[data-costing-field]");
        if (!input) return;
        if (e.key === "Enter") {
            e.preventDefault();
            if (input.dataset.line) saveLineField(input);
            else saveCostingField(input);
        } else if (e.key === "Escape" && input.value !== input.dataset.saved) {
            input.value = input.dataset.saved;
            clearFieldError(input);
        }
    });

    // ------------------------------------------------------------ removing and resetting lines

    root.addEventListener("click", function (e) {
        var remove = e.target.closest("[data-remove-line]");
        if (remove) { removeLine(remove); return; }
        var reset = e.target.closest("[data-line-reset]");
        if (reset) resetLine(reset);
    });

    function removeLine(button) {
        var description = button.dataset.description;
        var row = button.closest("[data-line-row]");
        var tableId = button.closest("#material-lines") ? "material-lines" : "rate-lines";
        var rows = Array.prototype.slice.call(document.querySelectorAll("#" + tableId + " [data-line-row]"));
        var position = rows.indexOf(row);

        TS.confirm("Remove " + description + "?",
            "It comes off this quote only. The catalogue and the rate card do not change.", "Remove", "danger")
            .then(function (yes) {
                if (!yes) return;
                TS.busy(button, true);
                saving(+1);
                totalsBusy(true);
                if (row) row.classList.add("is-saving");

                TS.api("DELETE", api + "lines/" + button.dataset.removeLine).then(function (result) {
                    if (result.ok) {
                        saveSucceeded();
                        TS.toast("success", "Line removed", description + " is no longer on this quote.");
                        return reloadTotals().then(refresh).then(function () {
                            saving(-1);
                            focusAfterRemoval(tableId, position);
                        });
                    }
                    saving(-1);
                    saveFailed();
                    totalsBusy(false);
                    TS.busy(button, false);
                    if (row) row.classList.remove("is-saving");
                    var said = describe(result, "The line was not removed");
                    TS.toast("error", said.title, said.detail);
                });
            });
    }

    // Focus goes to the row that took the removed one's place, or to the section heading.
    function focusAfterRemoval(tableId, position) {
        var rows = document.querySelectorAll("#" + tableId + " [data-line-row]");
        var target = rows[Math.min(position, rows.length - 1)];
        var field = target && target.querySelector("input, button");
        if (field) { field.focus(); return; }
        var heading = document.getElementById(tableId === "material-lines" ? "materials-title" : "rates-title");
        if (heading) {
            heading.setAttribute("tabindex", "-1");
            heading.focus();
        }
    }

    function resetLine(button) {
        var body = {};
        body[button.dataset.reset] = true;
        TS.busy(button, true);
        saving(+1);
        totalsBusy(true);

        TS.api("PUT", api + "lines/" + button.dataset.lineReset, body).then(function (result) {
            if (result.ok) {
                saveSucceeded();
                showTotals(result.data.totals);
                TS.toast("success", button.dataset.success || "Done");
                return refresh().then(function () { saving(-1); });
            }
            saving(-1);
            saveFailed();
            totalsBusy(false);
            TS.busy(button, false);
            var said = describe(result, "The change was not saved");
            TS.toast("error", said.title, said.detail);
        });
    }

    // ------------------------------------------------------------ the price boxes

    function skeleton(target) {
        target.replaceChildren();
        var box = el("div", "resolved-price is-loading");
        box.setAttribute("aria-hidden", "true");
        var a = el("div", "skeleton w-40");
        var b = el("div", "skeleton");
        box.appendChild(a);
        box.appendChild(b);
        target.appendChild(box);
    }

    function fact(label, value, note) {
        var block = el("div", "resolved-fact");
        block.appendChild(el("span", "qstrip-label", label));
        block.appendChild(el("span", label === "Price per sheet" || label === "Rate" ? "figure" : "strong", value));
        if (note) block.appendChild(el("span", "sub", note));
        return block;
    }

    /** The found price, with where it came from. */
    function showResolved(target, facts, origin, asAt) {
        target.replaceChildren();
        var box = el("div", "resolved-price");
        facts.forEach(function (f) { box.appendChild(f); });
        var from = el("div", "resolved-origin");
        from.appendChild(el("span", "line-origin", origin));
        from.appendChild(el("span", "sub", "Priced as at the quote date, " + asAt + "."));
        box.appendChild(from);
        target.appendChild(box);
    }

    /** The blocking error: no price, the reason, and what to do. Never a zero. */
    function showUnresolved(target, title, reason, advice) {
        target.replaceChildren();
        var box = el("div", "unresolved");
        box.id = target.id + "-error";
        box.setAttribute("role", "alert");
        box.appendChild(icon("i-alert"));
        var body = el("div");
        body.appendChild(el("strong", null, title));
        body.appendChild(el("span", "unresolved-reason", reason));
        if (advice) body.appendChild(el("span", "unresolved-advice", advice));
        box.appendChild(body);
        target.appendChild(box);
    }

    // ------------------------------------------------------------ the material cascade

    var materialForm = document.getElementById("add-material");
    if (materialForm) {
        var supplier = document.getElementById("supplier");
        var productLine = document.getElementById("product-line");
        var colour = document.getElementById("colour");
        var sheetSize = document.getElementById("sheet-size");
        var materialPrice = document.getElementById("material-price");
        var addMaterial = document.getElementById("add-material-button");
        var addMaterialWhy = document.getElementById("add-material-why");
        var quantity = document.getElementById("material-quantity");
        var discount = document.getElementById("material-discount");
        var priceUrl = root.dataset.priceUrl;
        var lookup = 0;

        var blockedMaterial = function (why) {
            addMaterial.disabled = true;
            addMaterial.setAttribute("aria-describedby", "add-material-why");
            addMaterialWhy.textContent = why;
        };

        var reset = function (select, prompt) {
            select.replaceChildren(new Option(prompt, ""));
            select.disabled = true;
            select.removeAttribute("aria-busy");
        };

        var load = function (select, url, describeOption, prompt, noun) {
            select.replaceChildren(new Option("Loading " + noun, ""));
            select.disabled = true;
            select.setAttribute("aria-busy", "true");

            return TS.api("GET", url).then(function (result) {
                select.removeAttribute("aria-busy");
                if (!result.ok) {
                    select.replaceChildren(new Option("Could not load the " + noun, ""));
                    var said = describe(result, "The " + noun + " could not be loaded");
                    TS.toast("error", said.title, said.detail + " Choose the previous step again to retry.");
                    return;
                }
                var items = result.data || [];
                if (items.length === 0) {
                    select.replaceChildren(new Option("None to choose from", ""));
                    announce("cascade-status", "There are no " + noun + " to choose from at this step.");
                    return;
                }
                select.replaceChildren(new Option(prompt, ""));
                items.forEach(function (item) {
                    var option = new Option(describeOption(item), item.id);
                    Object.keys(item).forEach(function (key) {
                        if (typeof item[key] !== "object") option.dataset[key] = item[key];
                    });
                    select.appendChild(option);
                });
                select.disabled = false;
                announce("cascade-status", items.length + " " + noun + " loaded. Choose one in the next list.");
            });
        };

        supplier.addEventListener("change", function () {
            reset(productLine, "Choose a supplier first");
            reset(colour, "Choose a product line first");
            reset(sheetSize, "Choose a colour first");
            materialPrice.replaceChildren();
            blockedMaterial("Choose all four steps to see the price.");
            if (!supplier.value) return;
            load(productLine, catalogue + "suppliers/" + supplier.value + "/product-lines",
                function (p) { return p.name + " (" + p.thicknessMm + " mm)"; },
                "Choose a product line", "product lines");
        });

        productLine.addEventListener("change", function () {
            reset(colour, "Choose a product line first");
            reset(sheetSize, "Choose a colour first");
            materialPrice.replaceChildren();
            blockedMaterial("Choose all four steps to see the price.");
            if (!productLine.value) return;
            load(colour, catalogue + "product-lines/" + productLine.value + "/colours",
                function (c) {
                    // Some suppliers price a whole range as one band of the same name; the band is shown only when it adds something.
                    var band = c.priceBand && c.priceBand !== c.name ? ", band " + c.priceBand : "";
                    return c.name + (c.supplierCode ? ", " + c.supplierCode : "") + band +
                        (c.status === "PhasingOut" ? " (being phased out)" : "");
                },
                "Choose a colour", "colours");
        });

        colour.addEventListener("change", function () {
            reset(sheetSize, "Choose a colour first");
            materialPrice.replaceChildren();
            blockedMaterial("Choose all four steps to see the price.");
            if (!colour.value) return;
            load(sheetSize, catalogue + "colours/" + colour.value + "/sheet-sizes",
                function (s) { return s.lengthMm + " x " + s.widthMm + " mm, " + F.area(s.areaM2); },
                "Choose a sheet size", "sheet sizes");
        });

        sheetSize.addEventListener("change", function () {
            materialPrice.replaceChildren();
            if (!sheetSize.value) { blockedMaterial("Choose all four steps to see the price."); return; }

            var option = sheetSize.options[sheetSize.selectedIndex];
            var size = option.dataset.lengthMm + " x " + option.dataset.widthMm + " mm";
            var area = F.area(Number(option.dataset.areaM2)) + " per sheet";
            var colourName = colour.options[colour.selectedIndex].dataset.name;
            var ticket = ++lookup;

            skeleton(materialPrice);
            blockedMaterial("Finding the price.");

            TS.api("GET", withQuery(priceUrl, { colourId: colour.value, sheetSizeId: sheetSize.value })).then(function (result) {
                if (ticket !== lookup) return;  // a later choice has replaced this one

                if (!result.ok) {
                    showUnresolved(materialPrice, "The price could not be checked",
                        TS.describeProblem(result.problem), "Choose the sheet size again to retry.");
                    blockedMaterial("The price could not be checked.");
                    return;
                }

                var p = result.data;
                if (p.resolved) {
                    showResolved(materialPrice,
                        [fact("Price per sheet", F.rand(p.unitPrice)), fact("Sheet size", size, area)],
                        p.origin, p.pricedAsAt);
                    addMaterial.disabled = false;
                    addMaterial.removeAttribute("aria-describedby");
                    addMaterialWhy.textContent = "";
                    announce("cascade-status", "Price found. " + F.rand(p.unitPrice) + " per sheet of " + colourName +
                        ", " + size + ". From " + p.origin + ".");
                } else {
                    showUnresolved(materialPrice, "No price for " + colourName + " in " + size, p.reason,
                        "Nothing has been added to the quote, because a line is never priced at zero. " +
                        "Choose another size or colour, or ask the Managing Director to add this price in the price editor.");
                    blockedMaterial("This material cannot be added until it has a price.");
                    addMaterial.setAttribute("aria-describedby", "add-material-why material-price-error");
                }
            });
        });

        materialForm.addEventListener("submit", function (e) {
            e.preventDefault();
            TS.clearFieldErrors(materialForm);
            [quantity, discount].forEach(clearFieldError);

            var bad = false;
            if (problemWith(quantity) || Number(quantity.value) <= 0) {
                fieldError(quantity, "Enter how many sheets, more than zero.");
                bad = true;
            }
            var discountProblem = problemWith(discount);
            if (discountProblem) { fieldError(discount, discountProblem); bad = true; }
            if (bad) { (quantity.getAttribute("aria-invalid") ? quantity : discount).focus(); return; }

            TS.busy(addMaterial, true);
            saving(+1);
            totalsBusy(true);

            TS.api("POST", api + "lines", {
                type: "material",
                colourId: Number(colour.value),
                sheetSizeId: Number(sheetSize.value),
                quantity: Number(quantity.value),
                supplierDiscountPercent: Number(discount.value || 0)
            }).then(function (result) {
                TS.busy(addMaterial, false);
                if (result.ok) {
                    saveSucceeded();
                    var line = result.data.line;
                    showTotals(result.data.totals);
                    TS.toast("success", "Material added",
                        line.description + ", " + F.quantity(line.quantity) + " sheet" + (line.quantity === 1 ? "" : "s") +
                        " at " + F.rand(line.unitPrice) + " each.");
                    quantity.value = "1";
                    discount.value = "0";
                    return refresh().then(function () { saving(-1); });
                }

                saving(-1);
                saveFailed();
                totalsBusy(false);
                var said = describe(result, "The material was not added");
                if (result.status === 422) {
                    showUnresolved(materialPrice, said.title, said.detail,
                        "Nothing has been added to the quote. Choose another material, or ask the Managing Director to add the price.");
                    blockedMaterial("This material cannot be added until it has a price.");
                } else if (result.status === 400 && result.problem.errors) {
                    TS.showFieldErrors(materialForm, result.problem.errors);
                } else {
                    TS.toast("error", said.title, said.detail);
                }
            });
        });
    }

    // ------------------------------------------------------------ the rate card grid
    //
    // Every rate card item has a quantity box. Typing a quantity adds the line;
    // clearing it, or typing 0, takes the line off. An item with no rate-card price
    // needs its rate typed first. Calculated items (sandpaper, silicon) are ticked in.

    function gridRow(input) { return input.closest("[data-grid-row]"); }

    function afterGridChange(result, success) {
        if (result.ok) {
            saveSucceeded();
            if (result.data && result.data.totals) showTotals(result.data.totals);
            if (success) TS.toast("success", success.title, success.detail || "");
            return (result.data && result.data.totals ? Promise.resolve() : reloadTotals()).then(refresh).then(function () { saving(-1); });
        }
        saving(-1);
        saveFailed();
        totalsBusy(false);
        var said = describe(result, "The change was not saved");
        TS.toast("error", said.title, said.detail);
        return Promise.resolve();
    }

    function saveGridQuantity(input) {
        if (input.value === input.dataset.saved || input.dataset.inflight === input.value) return;
        var row = gridRow(input);
        var name = row.dataset.name;
        var lineId = row.dataset.line;
        var raw = input.value.trim();
        var removing = raw === "" || Number(raw) === 0;

        if (!removing) {
            var problem = problemWith(input);
            if (problem) { fieldError(input, problem); return; }
        }
        clearFieldError(input);

        // Nothing on the quote and nothing typed: nothing to do.
        if (removing && !lineId) { input.dataset.saved = input.value; return; }

        var rateInput = row.querySelector("[data-grid-rate]");
        var resolved = row.dataset.resolved === "true";
        var typedRate = rateInput ? rateInput.value.trim() : "";

        if (!removing && !lineId && !resolved) {
            var rateProblem = typedRate === "" ? "Type the rate for this job first." : problemWith(rateInput);
            if (rateProblem) { fieldError(rateInput, rateProblem); rateInput.focus(); return; }
        }

        input.dataset.inflight = input.value;
        saving(+1);
        totalsBusy(true);
        row.classList.add("is-saving");

        var request;
        if (removing) {
            request = TS.api("DELETE", api + "lines/" + lineId).then(function (result) {
                return afterGridChange(result, { title: "Removed from the costing", detail: name });
            });
        } else if (lineId) {
            request = TS.api("PUT", api + "lines/" + lineId, { quantity: Number(raw) }).then(function (result) {
                return afterGridChange(result, null);
            });
        } else {
            var body = { type: "rate", rateItemId: Number(row.dataset.item), quantity: Number(raw) };
            if (!resolved) body.unitPrice = Number(typedRate);
            // A rate typed over the card price before the quantity becomes an override once the line exists.
            var override = resolved && typedRate !== "" && Number(typedRate) !== Number(row.dataset.card) ? Number(typedRate) : null;
            request = TS.api("POST", api + "lines", body).then(function (result) {
                if (result.ok && override !== null) {
                    return TS.api("PUT", api + "lines/" + result.data.line.id, { unitPrice: override }).then(function (second) {
                        return afterGridChange(second.ok ? second : result, null);
                    });
                }
                return afterGridChange(result, null);
            });
        }
        request.then(function () { delete input.dataset.inflight; row.classList.remove("is-saving"); });
    }

    function checkGridRate(input) {
        var raw = input.value.trim();
        if (raw === "") { clearFieldError(input); return; }
        var problem = problemWith(input);
        if (problem) fieldError(input, problem); else clearFieldError(input);
    }

    function toggleCalculated(box) {
        var row = gridRow(box);
        var name = row.dataset.name;
        saving(+1);
        totalsBusy(true);
        row.classList.add("is-saving");
        var request = box.checked
            ? TS.api("POST", api + "lines", { type: "rate", rateItemId: Number(row.dataset.item), quantity: 0 }).then(function (result) {
                return afterGridChange(result, { title: "Added to the costing", detail: name + ", with the quantity calculated from the materials." });
            })
            : TS.api("DELETE", api + "lines/" + row.dataset.line).then(function (result) {
                return afterGridChange(result, { title: "Removed from the costing", detail: name });
            });
        request.then(function () { row.classList.remove("is-saving"); });
    }

    root.addEventListener("change", function (e) {
        var qty = e.target.closest("input[data-grid-qty]");
        if (qty) { saveGridQuantity(qty); return; }
        var rate = e.target.closest("input[data-grid-rate]");
        if (rate) { checkGridRate(rate); return; }
        var include = e.target.closest("input[data-grid-include]");
        if (include) toggleCalculated(include);
    }, true);

    // Enter saves and moves down to the next item's quantity, as in a spreadsheet column.
    function nextQuantity(from) {
        var boxes = Array.prototype.slice.call(document.querySelectorAll("#rate-lines input[data-grid-qty], #rate-lines input[data-grid-include]"));
        var at = boxes.indexOf(from);
        return at >= 0 && at + 1 < boxes.length ? boxes[at + 1] : null;
    }

    root.addEventListener("keydown", function (e) {
        var qty = e.target.closest("input[data-grid-qty]");
        if (!qty) return;
        if (e.key === "Enter") {
            e.preventDefault();
            var next = nextQuantity(qty);
            if (next) next.focus();
            saveGridQuantity(qty);
        } else if (e.key === "Escape" && qty.value !== qty.dataset.saved) {
            qty.value = qty.dataset.saved;
            clearFieldError(qty);
        }
    });
})();
