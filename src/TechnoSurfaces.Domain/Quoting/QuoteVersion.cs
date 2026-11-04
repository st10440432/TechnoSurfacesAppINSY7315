namespace TechnoSurfaces.Domain.Quoting;

/// <summary>
/// An immutable snapshot of the contents of a quote, and the memento in the Memento
/// pattern described in the design document.
///
/// Required because the original offer must be retained when a customer
/// counter-offer causes a quote to be revised, and because the prices within a
/// quote must not move once it has been issued. Costing lines and quotation lines
/// belong to the version rather than to the quote; that is what renders the history
/// immutable.
///
/// A version is built, then sealed. After sealing, nothing can be added or changed:
/// a revision creates a new version instead.
/// </summary>
public class QuoteVersion
{
    private readonly List<QuoteItem> _items = new();
    private readonly List<CostingLine> _costingLines = new();
    private readonly List<QuotationLine> _quotationLines = new();
    private readonly List<QuoteVersionTerm> _terms = new();
    private readonly List<QuoteVersionWarranty> _warranties = new();
    private readonly List<QuoteBufferStock> _bufferStock = new();

    private QuoteVersion() { }

    public QuoteVersion(int versionNo, string createdByUserId, decimal markupPercent, decimal vatRate = 0.15m)
    {
        if (versionNo < 1)
            throw new ArgumentOutOfRangeException(nameof(versionNo), "Version numbers start at 1.");
        if (markupPercent < 0)
            throw new ArgumentOutOfRangeException(nameof(markupPercent), "A markup cannot be negative.");

        VersionNo = versionNo;
        CreatedByUserId = createdByUserId;
        MarkupPercent = markupPercent;
        VatRate = vatRate;
        CreatedAtUtc = DateTime.UtcNow;
        _items.Add(new QuoteItem(QuoteItem.FirstItemName, markupPercent) { SortOrder = 1 });
    }

    public int Id { get; private set; }
    public int QuoteId { get; private set; }
    public Quote? Quote { get; private set; }

    public int VersionNo { get; private set; }

    public string CreatedByUserId { get; private set; } = "";
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>
    /// The quote's markup: the one a new item starts with. Each item carries its own
    /// markup, applied to that item's sub-total only.
    /// </summary>
    public decimal MarkupPercent { get; private set; }

    /// <summary>
    /// Stored on the version rather than hard-coded, so that a historic quote keeps
    /// the rate it was created under if the rate ever changes.
    /// </summary>
    public decimal VatRate { get; private set; }

    /// <summary>
    /// Transport: the petrol and delivery amount the Managing Director types per
    /// job, based on the trips the job needs. Cost recovery, so it sits below the
    /// markup line with the cut-out charges. Not on the rate card.
    /// </summary>
    public decimal TransportAmount { get; private set; }

    /// <summary>
    /// Whether the material lines on the customer quotation give the number of sheets.
    /// Off by default: the client's customers read brand, colour and thickness only,
    /// so each material is one item. It can be turned on per quote.
    /// </summary>
    public bool ShowSheetCounts { get; private set; }

    /// <summary>
    /// The job's allowances, such as travel, inspection and time away for a tender out
    /// of town: one amount for the whole job, typed by the estimator. Cost recovery
    /// like transport, so it sits below the markup line and is not marked up.
    /// </summary>
    public decimal AllowanceAmount { get; private set; }

    /// <summary>What the allowance is called on the quotation, when it is not the usual wording.</summary>
    public string? AllowanceDescription { get; private set; }

    /// <summary>The allowance as the customer reads it.</summary>
    public string AllowanceWording => AllowanceDescription ?? DefaultAllowanceDescription;

    /// <summary>How the customer quotation lists the work: the written lines, or a bill of quantities.</summary>
    public QuotationLayout Layout { get; private set; }

    /// <summary>
    /// On a bill of quantities, the transport and the allowance are spread over the
    /// items' rates in proportion to their amounts, as the client prices a tender.
    /// When this is set they are listed as lines of their own after the items instead.
    /// </summary>
    public bool AllowancesOnOwnLines { get; private set; }

    public bool SpreadAllowances => !AllowancesOnOwnLines;

    /// <summary>The allowance's wording on the client's tenders.</summary>
    public const string DefaultAllowanceDescription = "Travel, inspection and away allowances";

    public const int AllowanceDescriptionMaxLength = 200;

    /// <summary>Once sealed the version is a read-only record.</summary>
    public bool IsSealed { get; private set; }

    /// <summary>
    /// The items on the quote, in the order they are listed. Never empty: a version
    /// saved by a release from before items, such as during a rollback, is read as
    /// one item at the quote's markup, which is what it was.
    /// </summary>
    public IReadOnlyList<QuoteItem> Items
    {
        get
        {
            if (_items.Count == 0)
                _items.Add(new QuoteItem(QuoteItem.FirstItemName, MarkupPercent) { SortOrder = 1 });
            return _items.OrderBy(i => i.SortOrder).ThenBy(i => i.Id).ToList();
        }
    }

    public IReadOnlyCollection<CostingLine> CostingLines => _costingLines.AsReadOnly();
    public IReadOnlyCollection<QuotationLine> QuotationLines => _quotationLines.AsReadOnly();

    /// <summary>Sheets ordered beyond what the items need, per material.</summary>
    public IReadOnlyCollection<QuoteBufferStock> BufferStock => _bufferStock.AsReadOnly();

    /// <summary>
    /// The standing wording this version was approved with. Empty until the
    /// version is approved; a version still being worked on prints the current
    /// wording instead.
    /// </summary>
    public IReadOnlyCollection<QuoteVersionTerm> Terms => _terms.AsReadOnly();

    /// <summary>The brand warranties this version was approved with.</summary>
    public IReadOnlyCollection<QuoteVersionWarranty> Warranties => _warranties.AsReadOnly();

    public bool HasRecordedTerms => _terms.Count > 0;

    // ---- What the customer was issued (US-21). Recorded once, when the version is
    // ---- approved, because the quote's own details move on after a reopen: the
    // ---- validity period starts again and the site or contact can be corrected.

    /// <summary>When the version was approved and issued. Null for a version never issued.</summary>
    public DateTime? IssuedAtUtc { get; private set; }

    /// <summary>Who approved it. The quote's own approver is cleared when it is reopened.</summary>
    public string? IssuedByUserId { get; private set; }

    public string? IssuedAttention { get; private set; }
    public string? IssuedCompany { get; private set; }
    public string? IssuedTel { get; private set; }
    public string? IssuedEmail { get; private set; }
    public string? IssuedSite { get; private set; }
    public string? IssuedProject { get; private set; }
    public string? IssuedCustomerReference { get; private set; }
    public DateOnly? IssuedValidUntil { get; private set; }

    public bool IsIssued => IssuedAtUtc is not null;

    /// <summary>
    /// Records the heading the quotation was issued with. Called by
    /// <see cref="Quote.Approve"/> immediately before the version is sealed, so an
    /// earlier version can be read later exactly as the customer received it.
    /// </summary>
    internal void RecordIssue(IssuedHeading heading, string issuedByUserId)
    {
        EnsureUnsealed();
        if (IsIssued)
            throw new InvalidOperationException($"Version {VersionNo} has already been issued.");

        IssuedAtUtc = DateTime.UtcNow;
        IssuedByUserId = issuedByUserId;
        IssuedAttention = heading.Attention;
        IssuedCompany = heading.Company;
        IssuedTel = heading.Tel;
        IssuedEmail = heading.Email;
        IssuedSite = heading.Site;
        IssuedProject = heading.Project;
        IssuedCustomerReference = heading.CustomerReference;
        IssuedValidUntil = heading.ValidUntil;
    }

    /// <summary>Adds a line to an item's costing; to the first item when none is named.</summary>
    public void AddCostingLine(CostingLine line, QuoteItem? item = null)
    {
        EnsureUnsealed();
        line.PlaceIn(item is null ? Items[0] : OwnItem(item));
        _costingLines.Add(line);
    }

    /// <summary>
    /// The item a costing line belongs to. A version read without tracking, as the
    /// quote list reads it, has the line's item id but not the item itself, so the
    /// item is found by its id. A line saved before items belongs to the first item.
    /// </summary>
    public QuoteItem ItemOf(CostingLine line)
    {
        if (line.Item is { } placed && _items.Contains(placed))
            return placed;
        if (line.QuoteItemId is int id && _items.FirstOrDefault(i => i.Id == id) is { } saved)
            return saved;
        return Items[0];
    }

    /// <summary>The costing lines of one item.</summary>
    public IEnumerable<CostingLine> LinesOf(QuoteItem item) => _costingLines.Where(l => ItemOf(l) == item);

    /// <summary>
    /// A new item, listed last, at the markup of the item before it unless another is
    /// given: the client mostly marks every item of a job up alike.
    /// </summary>
    public QuoteItem AddItem(string name, string? number = null, decimal quantity = 1m, decimal? markupPercent = null,
        string? unit = null, string? section = null)
    {
        EnsureUnsealed();
        var item = new QuoteItem(name, markupPercent ?? Items[^1].MarkupPercent, quantity, number)
        {
            SortOrder = _items.Count == 0 ? 1 : _items.Max(i => i.SortOrder) + 1
        };
        item.PlaceOnBill(unit, section);
        _items.Add(item);
        return item;
    }

    public void ChangeItem(QuoteItem item, string name, string? number, decimal quantity, string? unit = null, string? section = null)
    {
        EnsureUnsealed();
        OwnItem(item).Rename(name, number);
        item.SetQuantity(quantity);
        item.PlaceOnBill(unit, section);
    }

    /// <summary>
    /// Changes one item's markup, leaving the other items as they are. With one item
    /// it is the quote's markup too, so the two never disagree.
    /// </summary>
    public void SetItemMarkupPercent(QuoteItem item, decimal markupPercent)
    {
        EnsureUnsealed();
        OwnItem(item).SetMarkupPercent(markupPercent);
        if (_items.Count == 1)
            MarkupPercent = markupPercent;
    }

    /// <summary>
    /// Takes an item and its costing lines off the quote. The last item cannot be
    /// removed. Returns the costing lines taken off, for the caller to delete.
    /// </summary>
    public IReadOnlyList<CostingLine> RemoveItem(QuoteItem item)
    {
        EnsureUnsealed();
        OwnItem(item);
        if (_items.Count == 1)
            throw new InvalidOperationException("A quote keeps at least one item.");

        var lines = LinesOf(item).ToList();
        foreach (var line in lines)
            _costingLines.Remove(line);
        _items.Remove(item);
        if (_items.Count == 1)
            MarkupPercent = _items[0].MarkupPercent;
        return lines;
    }

    /// <summary>
    /// Prices an item at a rate per square metre of its material instead of by its
    /// markup, or by its markup again when no price is given.
    /// </summary>
    public void SetItemPricePerM2(QuoteItem item, decimal? pricePerM2)
    {
        EnsureUnsealed();
        OwnItem(item).PriceByArea(pricePerM2);
    }

    /// <summary>
    /// Sets the buffer stock of one material. No sheets takes the material's buffer
    /// off. Returns the record taken off, for the caller to delete.
    /// </summary>
    public QuoteBufferStock? SetBufferStock(string material, decimal sheets)
    {
        EnsureUnsealed();
        var existing = _bufferStock.FirstOrDefault(b => b.Material == material.Trim());
        if (sheets == 0)
        {
            if (existing is not null)
                _bufferStock.Remove(existing);
            return existing;
        }

        if (existing is null)
            _bufferStock.Add(new QuoteBufferStock(material, sheets));
        else
            existing.SetSheets(sheets);
        return null;
    }

    private QuoteItem OwnItem(QuoteItem item) =>
        _items.Contains(item) ? item : throw new InvalidOperationException($"{item.Name} is not an item on version {VersionNo}.");

    public void AddQuotationLine(QuotationLine line)
    {
        EnsureUnsealed();
        _quotationLines.Add(line);
    }

    public void RemoveCostingLine(CostingLine line)
    {
        EnsureUnsealed();
        _costingLines.Remove(line);
    }

    /// <summary>Rewrites a customer-facing line on this version (US-10).</summary>
    public void ChangeQuotationLine(QuotationLine line, string description, decimal amountExVat, string? room, decimal quantity) =>
        ChangeLine(line).Change(description, amountExVat, room, quantity);

    /// <summary>Takes a new amount and quantity from the costing for a copied line.</summary>
    public void FollowCosting(QuotationLine line, decimal amountExVat, decimal quantity) =>
        ChangeLine(line).FollowCosting(amountExVat, quantity);

    /// <summary>Ties a line copied before lines remembered their source to its costing.</summary>
    public void LinkQuotationLine(QuotationLine line, CostingLine? source, bool carriesOtherCosts)
    {
        if (source is not null && !_costingLines.Contains(source))
            throw new InvalidOperationException($"{source.Description} is not a line on version {VersionNo}.");
        ChangeLine(line).Follow(source, carriesOtherCosts);
    }

    private QuotationLine ChangeLine(QuotationLine line)
    {
        EnsureUnsealed();
        if (!_quotationLines.Contains(line))
            throw new InvalidOperationException($"That quotation line is not on version {VersionNo}.");
        return line;
    }

    public void RemoveQuotationLine(QuotationLine line)
    {
        EnsureUnsealed();
        _quotationLines.Remove(line);
    }

    /// <summary>
    /// Puts the customer-facing lines in the given order. Every line on the version
    /// must be named exactly once, so a reorder cannot drop or duplicate a line.
    /// </summary>
    public void ReorderQuotationLines(IReadOnlyList<QuotationLine> order)
    {
        EnsureUnsealed();
        if (order.Count != _quotationLines.Count || order.Distinct().Count() != order.Count
            || order.Any(l => !_quotationLines.Contains(l)))
            throw new ArgumentException("Name every quotation line on the version exactly once.", nameof(order));

        for (var i = 0; i < order.Count; i++)
            order[i].SortOrder = i + 1;
    }

    /// <summary>
    /// What the customer quotation adds up to before VAT. US-10: this must equal the
    /// costing's total excluding VAT before the quote is approved.
    /// </summary>
    public decimal QuotationSubtotalExVat() =>
        Round(_quotationLines.Sum(l => l.AmountExVat));

    /// <summary>Sets the quote's markup, and every item's with it.</summary>
    public void SetMarkupPercent(decimal markupPercent)
    {
        EnsureUnsealed();
        if (markupPercent < 0)
            throw new ArgumentOutOfRangeException(nameof(markupPercent), "A markup cannot be negative.");
        MarkupPercent = markupPercent;
        foreach (var item in _items)
            item.SetMarkupPercent(markupPercent);
    }

    public void SetShowSheetCounts(bool show)
    {
        EnsureUnsealed();
        ShowSheetCounts = show;
    }

    public void SetTransportAmount(decimal amount)
    {
        EnsureUnsealed();
        if (amount < 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "A transport amount cannot be negative.");
        TransportAmount = amount;
    }

    /// <summary>The job's allowance, and its wording on the quotation; no wording keeps the usual one.</summary>
    public void SetAllowance(decimal amount, string? description = null)
    {
        EnsureUnsealed();
        if (amount < 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "An allowance cannot be negative.");
        AllowanceAmount = amount;
        AllowanceDescription = string.IsNullOrWhiteSpace(description) || description.Trim() == DefaultAllowanceDescription
            ? null
            : description.Trim();
    }

    public void SetLayout(QuotationLayout layout)
    {
        EnsureUnsealed();
        if (!Enum.IsDefined(layout))
            throw new ArgumentOutOfRangeException(nameof(layout), "Not a quotation layout.");
        Layout = layout;
    }

    public void SetSpreadAllowances(bool spread)
    {
        EnsureUnsealed();
        AllowancesOnOwnLines = !spread;
    }

    /// <summary>
    /// Changes a quantity on this quote. On a derived line, such as silicon at two
    /// per sheet, the typed figure replaces the derived one until it is restored.
    /// </summary>
    public void ChangeQuantity(CostingLine line, decimal quantity) =>
        Change(line).ChangeQuantity(quantity);

    /// <summary>Returns a derived line to the quantity the calculator works out.</summary>
    public void RestoreDerivedQuantity(CostingLine line) =>
        Change(line).RestoreDerivedQuantity();

    /// <summary>
    /// Charges a line at a different rate on this quote only (US-06). The rate
    /// card is not changed and the catalogue price stays on the line.
    /// </summary>
    public void OverrideUnitPrice(CostingLine line, decimal unitPrice) =>
        Change(line).OverrideUnitPrice(unitPrice);

    public void ClearPriceOverride(CostingLine line) =>
        Change(line).ClearPriceOverride();

    /// <summary>A discount received from the supplier on a material line (US-08).</summary>
    public void ChangeSupplierDiscount(CostingLine line, decimal supplierDiscountPercent) =>
        Change(line).ChangeSupplierDiscount(supplierDiscountPercent);

    private CostingLine Change(CostingLine line)
    {
        EnsureUnsealed();
        if (!_costingLines.Contains(line))
            throw new InvalidOperationException($"{line.Description} is not a line on version {VersionNo}.");
        return line;
    }

    /// <summary>
    /// Closes the version. Called when the quote is approved, so the Managing
    /// Director can still correct a pending quote (US-18), and when a revision
    /// starts. After this the snapshot cannot change.
    /// </summary>
    public void Seal() => IsSealed = true;

    /// <summary>
    /// A new, open version that starts as a copy of this one. Every line keeps the
    /// price, origin, quantity, override and discount it carries here, and nothing
    /// is priced again, so a catalogue change since this version cannot move the
    /// revision's starting figures (Task 1 5.1.3). This version is read, never
    /// written.
    /// </summary>
    public QuoteVersion CreateRevision(int versionNo, string createdByUserId)
    {
        if (versionNo <= VersionNo)
            throw new ArgumentOutOfRangeException(nameof(versionNo), $"A revision of version {VersionNo} must have a higher number.");

        var revision = new QuoteVersion(versionNo, createdByUserId, MarkupPercent, VatRate)
        {
            TransportAmount = TransportAmount,
            ShowSheetCounts = ShowSheetCounts,
            AllowanceAmount = AllowanceAmount,
            AllowanceDescription = AllowanceDescription,
            Layout = Layout,
            AllowancesOnOwnLines = AllowancesOnOwnLines
        };

        // Each copied line belongs to the copy of its item, and a copied quotation
        // line follows the copy of its costing line, not the sealed original.
        revision._items.Clear();
        var items = new Dictionary<QuoteItem, QuoteItem>();
        foreach (var item in Items)
        {
            var copy = item.CopyForRevision();
            items[item] = copy;
            revision._items.Add(copy);
        }

        var copies = new Dictionary<CostingLine, CostingLine>();
        foreach (var line in _costingLines)
        {
            var copy = line.CopyForRevision();
            copy.PlaceIn(items[ItemOf(line)]);
            copies[line] = copy;
            revision._costingLines.Add(copy);
        }

        foreach (var buffer in _bufferStock)
            revision._bufferStock.Add(buffer.CopyForRevision());

        foreach (var line in _quotationLines)
            revision._quotationLines.Add(line.CopyForRevision(
                line.CostingLine is { } source && copies.TryGetValue(source, out var copied) ? copied : null));

        return revision;
    }

    /// <summary>
    /// Records the standing wording and brand warranties this version is approved
    /// with. Called once, immediately before the version is approved and sealed, so
    /// the quotation it was issued with can always be reproduced exactly. Refused
    /// on a sealed version, and refused a second time, because a recorded term is
    /// part of the issued record.
    /// </summary>
    public void RecordTerms(IEnumerable<QuoteVersionTerm> terms, IEnumerable<QuoteVersionWarranty> warranties)
    {
        EnsureUnsealed();
        if (HasRecordedTerms)
            throw new InvalidOperationException($"The terms for version {VersionNo} have already been recorded.");

        var recorded = terms.ToList();
        if (recorded.Count == 0)
            throw new ArgumentException("A version cannot be issued with no standing terms.", nameof(terms));

        _terms.AddRange(recorded);
        _warranties.AddRange(warranties);
    }

    private void EnsureUnsealed()
    {
        if (IsSealed)
            throw new InvalidOperationException(
                $"Version {VersionNo} is sealed. Revise the quote to create a new version instead of altering this one.");
    }

    // ---- The calculation. Order of operations confirmed by the client. ----
    //
    // Each item is costed for one of it: its own lines, its own markup on its own
    // sub-total, and its own cut-outs and grooves after the markup. The quote adds
    // up each item as many times as the job needs it, then transport and the
    // allowance once.

    /// <summary>Sum of one item's line totals above the line.</summary>
    public decimal SubTotalExVat(QuoteItem item) =>
        Round(LinesOf(item).Where(l => !l.IsBelowTheLine).Sum(l => l.LineTotal()));

    /// <summary>
    /// Markup applies to the item's sub-total only, never to below-the-line items. An
    /// item priced per square metre is charged its area at that price instead, and its
    /// markup is what that price gives over its costing, which can be less than nothing.
    /// </summary>
    public decimal MarkupAmount(QuoteItem item) =>
        item.PricePerM2 is { } pricePerM2
            ? Round(Round(AreaM2(item) * pricePerM2) - SubTotalExVat(item))
            : Round(SubTotalExVat(item) * item.MarkupPercent / 100m);

    /// <summary>One item's cut-out and groove charges: cost recovery, not marked up.</summary>
    public decimal BelowTheLineTotal(QuoteItem item) =>
        Round(LinesOf(item).Where(l => l.IsBelowTheLine).Sum(l => l.LineTotal()));

    /// <summary>One of the item, ex VAT: its rate.</summary>
    public decimal TotalExVat(QuoteItem item) =>
        Round(SubTotalExVat(item) + MarkupAmount(item) + BelowTheLineTotal(item));

    /// <summary>The item as many times as the job needs it, ex VAT.</summary>
    public decimal AmountExVat(QuoteItem item) =>
        Round(TotalExVat(item) * item.Quantity);

    /// <summary>Square metres of material for one of the item.</summary>
    public decimal AreaM2(QuoteItem item) =>
        decimal.Round(LinesOf(item).Sum(l => l.AreaM2()), 4);

    /// <summary>Sheets of material for one of the item.</summary>
    public decimal SheetCount(QuoteItem item) =>
        LinesOf(item).Sum(l => l.SheetCount());

    /// <summary>Sum of every item's sub-total, each as many times as it is needed.</summary>
    public decimal SubTotalExVat() =>
        Round(Items.Sum(i => SubTotalExVat(i) * i.Quantity));

    public decimal MarkupAmount() =>
        Round(Items.Sum(i => MarkupAmount(i) * i.Quantity));

    /// <summary>
    /// Below-the-line items are cost recovery and are not marked up: the cut-out
    /// and groove charges, transport and the allowance.
    /// </summary>
    public decimal BelowTheLineTotal() =>
        Round(Items.Sum(i => BelowTheLineTotal(i) * i.Quantity) + TransportAmount + AllowanceAmount);

    /// <summary>What the job carries beside its items: transport and the allowance.</summary>
    public decimal JobExtras() =>
        Round(TransportAmount + AllowanceAmount);

    public decimal TotalExVat() =>
        Round(Items.Sum(AmountExVat) + JobExtras());

    public decimal VatAmount() =>
        Round(TotalExVat() * VatRate);

    public decimal TotalIncVat() =>
        Round(TotalExVat() + VatAmount());

    /// <summary>Total square metres of material on the quote.</summary>
    public decimal TotalAreaM2() =>
        decimal.Round(Items.Sum(i => AreaM2(i) * i.Quantity), 4);

    /// <summary>Total sheets of material on the quote.</summary>
    public decimal TotalSheetCount() =>
        Items.Sum(i => SheetCount(i) * i.Quantity);

    private static decimal Round(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}

/// <summary>The heading of the customer quotation at the moment a version was issued.</summary>
public sealed record IssuedHeading(
    string Attention,
    string Company,
    string? Tel,
    string? Email,
    string? Site,
    string? Project,
    string? CustomerReference,
    DateOnly ValidUntil);
