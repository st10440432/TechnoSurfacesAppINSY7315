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
    private readonly List<CostingLine> _costingLines = new();
    private readonly List<QuotationLine> _quotationLines = new();
    private readonly List<QuoteVersionTerm> _terms = new();
    private readonly List<QuoteVersionWarranty> _warranties = new();

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
    }

    public int Id { get; private set; }
    public int QuoteId { get; private set; }
    public Quote? Quote { get; private set; }

    public int VersionNo { get; private set; }

    public string CreatedByUserId { get; private set; } = "";
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Editable per quotation, and applied to the sub-total only.</summary>
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

    /// <summary>Once sealed the version is a read-only record.</summary>
    public bool IsSealed { get; private set; }

    public IReadOnlyCollection<CostingLine> CostingLines => _costingLines.AsReadOnly();
    public IReadOnlyCollection<QuotationLine> QuotationLines => _quotationLines.AsReadOnly();

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

    public void AddCostingLine(CostingLine line)
    {
        EnsureUnsealed();
        _costingLines.Add(line);
    }

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
    public void ChangeQuotationLine(QuotationLine line, string description, decimal amountExVat, string? room, decimal quantity)
    {
        EnsureUnsealed();
        if (!_quotationLines.Contains(line))
            throw new InvalidOperationException($"That quotation line is not on version {VersionNo}.");
        line.Change(description, amountExVat, room, quantity);
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

    public void SetMarkupPercent(decimal markupPercent)
    {
        EnsureUnsealed();
        if (markupPercent < 0)
            throw new ArgumentOutOfRangeException(nameof(markupPercent), "A markup cannot be negative.");
        MarkupPercent = markupPercent;
    }

    public void SetTransportAmount(decimal amount)
    {
        EnsureUnsealed();
        if (amount < 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "A transport amount cannot be negative.");
        TransportAmount = amount;
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
            TransportAmount = TransportAmount
        };

        foreach (var line in _costingLines)
            revision._costingLines.Add(line.CopyForRevision());

        foreach (var line in _quotationLines)
            revision._quotationLines.Add(line.CopyForRevision());

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

    /// <summary>Sum of line totals above the line.</summary>
    public decimal SubTotalExVat() =>
        Round(_costingLines.Where(l => !l.IsBelowTheLine).Sum(l => l.LineTotal()));

    /// <summary>Markup applies to the sub-total only, never to below-the-line items.</summary>
    public decimal MarkupAmount() =>
        Round(SubTotalExVat() * MarkupPercent / 100m);

    /// <summary>
    /// Below-the-line items are cost recovery and are not marked up: the cut-out
    /// and groove charges, and transport.
    /// </summary>
    public decimal BelowTheLineTotal() =>
        Round(_costingLines.Where(l => l.IsBelowTheLine).Sum(l => l.LineTotal()) + TransportAmount);

    public decimal TotalExVat() =>
        Round(SubTotalExVat() + MarkupAmount() + BelowTheLineTotal());

    public decimal VatAmount() =>
        Round(TotalExVat() * VatRate);

    public decimal TotalIncVat() =>
        Round(TotalExVat() + VatAmount());

    /// <summary>Total square metres of material on the quote.</summary>
    public decimal TotalAreaM2() =>
        decimal.Round(_costingLines.Sum(l => l.AreaM2()), 4);

    /// <summary>Total sheets of material on the quote.</summary>
    public decimal TotalSheetCount() =>
        _costingLines.Sum(l => l.SheetCount());

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
