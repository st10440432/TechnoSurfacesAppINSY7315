/* ==========================================================================
   Techno Surfaces: the lines on the customer quotation

   Adds, changes, removes and reorders the lines the customer reads, through the
   quotation API. After each change the page reloads, so the document below the
   editor always shows exactly what the customer will receive, and a message
   confirms what happened.
   ========================================================================== */

(function () {
    "use strict";

    var editor = document.getElementById("quotation-editor");
    if (!editor || !window.TS) return;

    var TS = window.TS;
    var api = editor.dataset.api;
    var form = document.getElementById("quotation-line-form");
    var title = document.getElementById("line-form-title");
    var submit = form.querySelector('button[type="submit"]');
    var cancel = form.querySelector("[data-cancel-edit]");
    var fill = form.querySelector("[data-fill-remaining]");
    var editing = null;

    function field(name) { return form.elements.namedItem(name); }

    function setMode(row) {
        editing = row ? row.dataset.quotationLine : null;
        title.textContent = row ? title.dataset.editTitle : title.dataset.addTitle;
        submit.textContent = row ? submit.dataset.editLabel : submit.dataset.addLabel;
        cancel.hidden = !row;
        if (fill) fill.hidden = !!row;
        TS.clearFieldErrors(form);

        field("room").value = row ? row.dataset.room || "" : "";
        field("description").value = row ? row.dataset.description || "" : "";
        field("quantity").value = row ? row.dataset.quantity : "1";
        field("amountExVat").value = row ? row.dataset.amount : "";
    }

    function done(title, message) {
        TS.storeFlash("success", title, message);
        window.location.reload();
    }

    function failed(result, fallback) {
        var heading = result.problem && result.problem.title ? result.problem.title : fallback;
        if (result.status === 400 && result.problem && result.problem.errors) {
            TS.showFieldErrors(form, result.problem.errors);
        }
        TS.toast("error", heading, TS.describeProblem(result.problem));
    }

    // Checked here first, so a mistake is shown at once beside the field.
    function validate() {
        var errors = {};
        var description = field("description").value.trim();
        var quantity = field("quantity").value.trim();
        var amount = field("amountExVat").value.trim();
        if (!description) errors.description = ["Describe the work for the customer."];
        if (quantity === "" || isNaN(Number(quantity)) || Number(quantity) < 0) errors.quantity = ["Enter 0 or more."];
        if (amount === "" || isNaN(Number(amount)) || Number(amount) < 0) errors.amountExVat = ["Enter the amount for this line, 0 or more."];
        return errors;
    }

    form.addEventListener("submit", function (e) {
        e.preventDefault();
        var errors = validate();
        if (Object.keys(errors).length) { TS.showFieldErrors(form, errors); return; }
        TS.clearFieldErrors(form);

        var body = {
            room: field("room").value.trim() || null,
            description: field("description").value.trim(),
            quantity: Number(field("quantity").value),
            amountExVat: Number(field("amountExVat").value)
        };

        TS.busy(submit, true);
        var request = editing
            ? TS.api("PUT", api + "quotation-lines/" + editing, body)
            : TS.api("POST", api + "quotation-lines", body);

        request.then(function (result) {
            if (result.ok) {
                done(editing ? "Line saved" : "Line added", "The quotation below shows it as the customer will see it.");
                return;
            }
            TS.busy(submit, false);
            failed(result, editing ? "The line was not saved" : "The line was not added");
        });
    });

    cancel.addEventListener("click", function () {
        setMode(null);
        field("room").focus();
    });

    if (fill) {
        fill.addEventListener("click", function () {
            field("amountExVat").value = editor.dataset.toAllocate;
            field("amountExVat").focus();
        });
    }

    editor.addEventListener("click", function (e) {
        var row = e.target.closest("[data-quotation-line]");
        if (!row) return;

        if (e.target.closest("[data-edit-line]")) {
            setMode(row);
            field("description").focus();
            return;
        }

        var del = e.target.closest("[data-delete-line]");
        if (del) {
            var name = row.dataset.room || "this line";
            TS.confirm("Remove " + name + " from the quotation?", "The costing does not change.", "Remove", "danger").then(function (yes) {
                if (!yes) return;
                TS.busy(del, true);
                TS.api("DELETE", api + "quotation-lines/" + row.dataset.quotationLine).then(function (result) {
                    if (result.ok) { done("Line removed", ""); return; }
                    TS.busy(del, false);
                    failed(result, "The line was not removed");
                });
            });
            return;
        }

        var move = e.target.closest("[data-move]");
        if (move) {
            var rows = Array.prototype.slice.call(editor.querySelectorAll("[data-quotation-line]"));
            var at = rows.indexOf(row);
            var to = move.dataset.move === "up" ? at - 1 : at + 1;
            if (to < 0 || to >= rows.length) return;
            var order = rows.map(function (r) { return Number(r.dataset.quotationLine); });
            order.splice(to, 0, order.splice(at, 1)[0]);

            TS.busy(move, true);
            TS.api("PUT", api + "quotation-lines/order", { lineIds: order }).then(function (result) {
                if (result.ok) { done("Order saved", ""); return; }
                TS.busy(move, false);
                failed(result, "The order was not saved");
            });
        }
    });
})();
