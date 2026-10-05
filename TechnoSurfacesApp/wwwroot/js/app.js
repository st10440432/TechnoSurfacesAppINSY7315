/* ==========================================================================
   Techno Surfaces: shared behaviour for every screen

   Everything a screen needs to talk to the /api endpoints and to tell the user
   what happened. No screen writes its own fetch call, its own message or its own
   busy state; they all come from here so the feedback is the same everywhere.

   No inline script is used anywhere in the views. Behaviour is attached here
   through data attributes, which keeps the pages compatible with a strict
   Content Security Policy.
   ========================================================================== */

(function () {
    "use strict";

    // ---------------------------------------------------------------- formatting
    // Matches the server's Fmt helper: R6 300,00 with a space for thousands.

    function grouped(value, places) {
        var fixed = Math.abs(Number(value) || 0).toFixed(places);
        var parts = fixed.split(".");
        var whole = parts[0].replace(/\B(?=(\d{3})+(?!\d))/g, " ");
        var sign = Number(value) < 0 ? "−" : "";
        return sign + whole + (parts[1] ? "," + parts[1] : "");
    }

    var format = {
        rand: function (v) { return "R" + grouped(v, 2); },
        number: function (v, places) { return grouped(v, places === undefined ? 2 : places); },
        quantity: function (v) {
            var n = Number(v) || 0;
            return Number.isInteger(n) ? grouped(n, 0) : grouped(n, 2).replace(/,?0+$/, "");
        },
        area: function (v) { return grouped(v, 2) + " m²"; },
        percent: function (v) {
            var n = Number(v) || 0;
            return (Number.isInteger(n) ? grouped(n, 0) : grouped(n, 2).replace(/,?0+$/, "")) + "%";
        }
    };

    // ---------------------------------------------------------------- the API

    function antiforgeryToken() {
        var meta = document.querySelector('meta[name="request-verification-token"]');
        return meta ? meta.getAttribute("content") : "";
    }

    /**
     * Calls an /api endpoint. Every POST, PUT and DELETE carries the antiforgery
     * token in the RequestVerificationToken header, which the global filter checks.
     * Resolves to { ok, status, data, problem }. Never throws for an HTTP error:
     * the caller decides what to show.
     */
    function api(method, url, body) {
        var options = {
            method: method,
            credentials: "same-origin",
            headers: { "Accept": "application/json" }
        };
        if (method !== "GET") {
            options.headers["RequestVerificationToken"] = antiforgeryToken();
        }
        if (body !== undefined) {
            options.headers["Content-Type"] = "application/json";
            options.body = JSON.stringify(body);
        }

        return fetch(url, options).then(function (response) {
            if (response.status === 401) {
                toast("error", "Your session has ended", "Sign in again to carry on. Nothing has been saved since.");
                setTimeout(function () { window.location.href = "/Account/Login?returnUrl=" + encodeURIComponent(location.pathname); }, 2500);
            }
            if (response.status === 204) return { ok: true, status: 204, data: null, problem: null };

            return response.text().then(function (text) {
                var json = null;
                try { json = text ? JSON.parse(text) : null; } catch (e) { json = null; }
                return response.ok
                    ? { ok: true, status: response.status, data: json, problem: null }
                    : { ok: false, status: response.status, data: null, problem: json || { title: "Something went wrong", detail: "The server answered " + response.status + "." } };
            });
        }, function () {
            return {
                ok: false, status: 0, data: null,
                problem: { title: "No connection", detail: "The request did not reach the server. Check the connection and try again." }
            };
        });
    }

    /** Turns a ProblemDetails body into one readable sentence. */
    function describeProblem(problem) {
        if (!problem) return "Something went wrong.";
        if (problem.errors) {
            var messages = [];
            Object.keys(problem.errors).forEach(function (key) {
                (problem.errors[key] || []).forEach(function (m) { messages.push(m); });
            });
            if (messages.length) return messages.join(" ");
        }
        return problem.detail || problem.title || "Something went wrong.";
    }

    // ---------------------------------------------------------------- toasts

    var icons = { success: "#i-check-circle", error: "#i-alert", info: "#i-info" };

    /**
     * Shows the result of an action. Success and information are announced
     * politely; an error is announced at once. Errors stay until dismissed.
     */
    function toast(kind, title, message) {
        var region = document.querySelector(kind === "error" ? "#toasts-urgent" : "#toasts");
        if (!region) return;

        var item = document.createElement("div");
        item.className = "toast-msg " + kind;

        var svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
        svg.setAttribute("class", "ico");
        svg.setAttribute("aria-hidden", "true");
        var use = document.createElementNS("http://www.w3.org/2000/svg", "use");
        use.setAttribute("href", icons[kind] || icons.info);
        svg.appendChild(use);

        var bodyEl = document.createElement("div");
        bodyEl.className = "toast-body";
        var titleEl = document.createElement("span");
        titleEl.className = "toast-title";
        titleEl.textContent = title;
        bodyEl.appendChild(titleEl);
        if (message) {
            var text = document.createElement("span");
            text.textContent = message;
            bodyEl.appendChild(text);
        }

        var close = document.createElement("button");
        close.type = "button";
        close.className = "toast-close";
        close.setAttribute("aria-label", "Dismiss message");
        close.textContent = "×";
        close.addEventListener("click", function () { item.remove(); });

        item.appendChild(svg);
        item.appendChild(bodyEl);
        item.appendChild(close);
        region.appendChild(item);

        if (kind !== "error") {
            setTimeout(function () { item.remove(); }, 6000);
        }
    }

    // ---------------------------------------------------------------- busy state

    function busy(button, on) {
        if (!button) return;
        if (on) {
            button.setAttribute("aria-busy", "true");
            button.setAttribute("aria-disabled", "true");
            button.dataset.wasDisabled = button.disabled ? "1" : "";
            button.disabled = true;
        } else {
            button.removeAttribute("aria-busy");
            button.removeAttribute("aria-disabled");
            button.disabled = button.dataset.wasDisabled === "1";
        }
    }

    // ---------------------------------------------------------------- confirm dialog

    var tones = { danger: "btn-danger", confirm: "btn-confirm", primary: "btn-primary" };

    /**
     * A native dialog, so focus stays inside it and Escape cancels. Cancel comes
     * first, so it has focus when the dialog opens. The tone colours the confirm
     * button: danger for removing or deactivating, confirm for approving.
     */
    function confirmAction(title, message, confirmLabel, tone) {
        var dialog = document.getElementById("confirm-dialog");
        if (!dialog || typeof dialog.showModal !== "function") {
            return Promise.resolve(window.confirm(message || title));
        }
        dialog.querySelector("[data-confirm-title]").textContent = title;
        dialog.querySelector("[data-confirm-message]").textContent = message || "";
        var ok = dialog.querySelector("[data-confirm-ok]");
        ok.textContent = confirmLabel || "Confirm";
        ok.className = "btn " + (tones[tone] || tones.primary);

        var form = dialog.querySelector("form");

        return new Promise(function (resolve) {
            var settled = false;
            function done(result) {
                if (settled) return;
                settled = true;
                form.removeEventListener("submit", onSubmit);
                dialog.removeEventListener("cancel", onCancel);
                dialog.removeEventListener("close", onClose);
                resolve(result);
            }
            // The button pressed is read from the submit event, which fires at once;
            // close and Escape are the fallbacks.
            function onSubmit(e) { done(!!e.submitter && e.submitter.value === "ok"); }
            function onCancel() { done(false); }
            function onClose() { done(dialog.returnValue === "ok"); }
            form.addEventListener("submit", onSubmit);
            dialog.addEventListener("cancel", onCancel);
            dialog.addEventListener("close", onClose);
            dialog.returnValue = "";
            dialog.showModal();
        });
    }

    // ---------------------------------------------------------------- form errors

    /**
     * Marks the fields named in a ValidationProblemDetails body. Each field gets
     * aria-invalid and is tied to its message with aria-describedby, so a screen
     * reader reads the error when the field gains focus.
     */
    function showFieldErrors(form, errors) {
        clearFieldErrors(form);
        var first = null;
        Object.keys(errors || {}).forEach(function (key) {
            var name = key.charAt(0).toLowerCase() + key.slice(1);
            var field = form.querySelector('[name="' + name + '"], [name="' + key + '"]');
            if (!field) return;
            var id = (field.id || name) + "-error";
            var message = document.createElement("span");
            message.className = "field-error";
            message.id = id;
            message.setAttribute("data-generated-error", "");
            message.textContent = (errors[key] || []).join(" ");
            (field.closest(".input-group") || field).insertAdjacentElement("afterend", message);
            field.setAttribute("aria-invalid", "true");
            var described = (field.getAttribute("aria-describedby") || "").split(" ").filter(Boolean);
            described.push(id);
            field.setAttribute("aria-describedby", described.join(" "));
            if (!first) first = field;
        });
        if (first) first.focus();
    }

    function clearFieldErrors(form) {
        form.querySelectorAll("[data-generated-error]").forEach(function (el) {
            var field = form.querySelector('[aria-describedby~="' + el.id + '"]');
            if (field) {
                field.removeAttribute("aria-invalid");
                var rest = field.getAttribute("aria-describedby").split(" ").filter(function (x) { return x !== el.id; });
                if (rest.length) field.setAttribute("aria-describedby", rest.join(" "));
                else field.removeAttribute("aria-describedby");
            }
            el.remove();
        });
    }

    // ---------------------------------------------------------------- action buttons
    // <button data-api-action data-method="POST" data-url="/api/quotes/4/submit"
    //         data-confirm="Submit for approval?" data-success="Submitted for approval">

    function wireActionButtons() {
        document.addEventListener("click", function (event) {
            var button = event.target.closest("[data-api-action]");
            if (!button) return;
            event.preventDefault();

            var ask = button.dataset.confirm
                ? confirmAction(button.dataset.confirm, button.dataset.confirmDetail, button.dataset.confirmLabel, button.dataset.confirmTone)
                : Promise.resolve(true);

            ask.then(function (yes) {
                if (!yes) return;
                busy(button, true);
                api(button.dataset.method || "POST", button.dataset.url).then(function (result) {
                    if (result.ok) {
                        var after = function () {
                            if (button.dataset.redirect) window.location.href = button.dataset.redirect;
                            else window.location.reload();
                        };
                        storeFlash("success", button.dataset.success || "Done", button.dataset.successDetail || "");
                        after();
                    } else {
                        busy(button, false);
                        toast("error", result.problem && result.problem.title ? result.problem.title : "That did not work",
                            describeProblem(result.problem));
                    }
                });
            });
        });
    }

    // ---------------------------------------------------------------- API forms
    // <form data-api-form data-method="POST" data-url="/api/quotes/4/invoice"
    //       data-success="Invoice recorded" data-redirect="/Quotes/Costing/4">
    // The fields are sent as JSON: number inputs as numbers, empty fields as null.

    function formBody(form) {
        var body = {};
        Array.prototype.forEach.call(form.elements, function (field) {
            if (!field.name || field.disabled || field.name === "__RequestVerificationToken") return;
            if (field.type === "submit" || field.type === "button") return;
            if (field.type === "checkbox") { body[field.name] = field.checked; return; }
            if (field.type === "radio") { if (field.checked) body[field.name] = field.value; return; }
            var value = field.value.trim();
            if (value === "") body[field.name] = null;
            else if (field.type === "number" || field.hasAttribute("data-number")) body[field.name] = Number(value);
            else body[field.name] = value;
        });
        return body;
    }

    /** The field's label in words, without the required mark or "optional". */
    function labelFor(form, field) {
        var label = field.id ? form.querySelector('label[for="' + field.id + '"]') : null;
        if (!label) return "value";
        var copy = label.cloneNode(true);
        copy.querySelectorAll(".req, .optional, .sr-only").forEach(function (n) { n.remove(); });
        var text = copy.textContent.trim();
        return text.charAt(0).toLowerCase() + text.slice(1);
    }

    /**
     * Checks the fields in the browser first, so a mistake is explained in plain
     * words at once. The server checks again and has the last word.
     */
    function clientErrors(form) {
        var errors = {};
        Array.prototype.forEach.call(form.elements, function (field) {
            if (!field.name || field.disabled || !field.willValidate) return;
            var value = (field.value || "").trim();
            var message = null;
            if (field.required && value === "") {
                message = (field.tagName === "SELECT" ? "Choose the " : "Enter the ") + labelFor(form, field) + ".";
            } else if (value !== "" && field.type === "number") {
                var n = Number(value);
                var min = field.getAttribute("min");
                var max = field.getAttribute("max");
                if (isNaN(n)) message = "Enter a number.";
                else if (min !== null && n < Number(min)) message = Number(min) > 0 ? "Enter an amount greater than zero." : "Enter 0 or more.";
                else if (max !== null && n > Number(max)) message = "Enter " + max + " or less.";
            } else if (value !== "" && field.type === "email" && field.validity.typeMismatch) {
                message = "Enter an email address like name@example.co.za.";
            }
            if (message) errors[field.name] = [message];
        });
        return errors;
    }

    function wireApiForms() {
        document.addEventListener("submit", function (event) {
            var form = event.target.closest("form[data-api-form]");
            if (!form) return;
            event.preventDefault();
            clearFieldErrors(form);

            var problems = clientErrors(form);
            if (Object.keys(problems).length) {
                showFieldErrors(form, problems);
                return;
            }

            var button = form.querySelector('button[type="submit"]');
            busy(button, true);

            api(form.dataset.method || "POST", form.dataset.url, formBody(form)).then(function (result) {
                if (result.ok) {
                    storeFlash("success", form.dataset.success || "Saved", form.dataset.successDetail || "");
                    // data-redirect-to="/Customers/Details/{id}" opens the record just created.
                    var to = form.dataset.redirectTo && result.data && result.data.id !== undefined
                        ? form.dataset.redirectTo.replace("{id}", encodeURIComponent(result.data.id))
                        : form.dataset.redirect;
                    if (to) window.location.href = to;
                    else window.location.reload();
                    return;
                }
                busy(button, false);
                if (result.problem && result.problem.errors) {
                    showFieldErrors(form, result.problem.errors);
                    toast("error", "Check the form", describeProblem(result.problem));
                } else {
                    toast("error", result.problem && result.problem.title ? result.problem.title : "That did not work",
                        describeProblem(result.problem));
                }
            });
        });
    }

    // An error summary takes focus when the page loads, so it is heard first.
    function focusOnLoad() {
        var target = document.querySelector("[data-focus-on-load]");
        if (target) target.focus();
    }

    // A message that survives the reload after an action, shown on the next page.
    function storeFlash(kind, title, message) {
        try { sessionStorage.setItem("ts-flash", JSON.stringify({ kind: kind, title: title, message: message })); } catch (e) { }
    }

    function showStoredFlash() {
        try {
            var raw = sessionStorage.getItem("ts-flash");
            if (raw) {
                sessionStorage.removeItem("ts-flash");
                var flash = JSON.parse(raw);
                toast(flash.kind, flash.title, flash.message);
            }
        } catch (e) { }

        // A message the server set for this page.
        var server = document.getElementById("server-flash");
        if (server) toast(server.dataset.kind || "info", server.dataset.title || "", server.dataset.message || "");
    }

    // ---------------------------------------------------------------- navigation

    function wireNavigation() {
        var toggles = document.querySelectorAll("[data-nav-toggle]");
        function setOpen(open) {
            document.body.classList.toggle("nav-open", open);
            toggles.forEach(function (t) { t.setAttribute("aria-expanded", open ? "true" : "false"); });
            if (open) {
                var firstLink = document.querySelector(".sidebar .nav-link");
                if (firstLink) firstLink.focus();
            }
        }
        toggles.forEach(function (t) {
            t.addEventListener("click", function () { setOpen(!document.body.classList.contains("nav-open")); });
        });
        var scrim = document.querySelector(".sidebar-scrim");
        if (scrim) scrim.addEventListener("click", function () { setOpen(false); });
        document.addEventListener("keydown", function (e) {
            if (e.key === "Escape" && document.body.classList.contains("nav-open")) {
                setOpen(false);
                if (toggles[0]) toggles[0].focus();
            }
        });
    }

    // Selects that submit their filter form when changed.
    function wireAutoSubmit() {
        document.addEventListener("change", function (e) {
            if (e.target.matches("[data-autosubmit]") && e.target.form) e.target.form.submit();
        });
    }

    // Print buttons.
    function wirePrint() {
        document.addEventListener("click", function (e) {
            if (e.target.closest("[data-print]")) { e.preventDefault(); window.print(); }
        });
    }

    // Forms posted the normal way show a busy button while the page loads.
    function wireFormBusy() {
        document.addEventListener("submit", function (e) {
            var form = e.target;
            // A dialog's own form only closes the dialog, so it is never busy.
            if (form.hasAttribute("data-no-busy") || form.getAttribute("method") === "dialog" || e.defaultPrevented) return;
            var button = form.querySelector('button[type="submit"]:not([formnovalidate])');
            if (button && form.checkValidity()) busy(button, true);
        });
    }

    document.addEventListener("DOMContentLoaded", function () {
        wireNavigation();
        wireActionButtons();
        wireApiForms();
        wireAutoSubmit();
        wirePrint();
        wireFormBusy();
        showStoredFlash();
        focusOnLoad();
    });

    window.TS = {
        api: api,
        toast: toast,
        busy: busy,
        confirm: confirmAction,
        describeProblem: describeProblem,
        showFieldErrors: showFieldErrors,
        clearFieldErrors: clearFieldErrors,
        storeFlash: storeFlash,
        format: format
    };
})();
