namespace TechnoSurfaces.Domain.Quoting;

/// <summary>
/// One line of standing wording as it stood when a version was approved. Copied
/// from <see cref="QuotationTerm"/> so that a later change to the wording cannot
/// alter what an issued quotation said, in the same way a costing line copies its
/// price (US-21, US-22). Never edited after it is recorded.
/// </summary>
public class QuoteVersionTerm
{
    private QuoteVersionTerm() { }

    public QuoteVersionTerm(TermSection section, string text, int sortOrder)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("A recorded term needs its wording.", nameof(text));

        Section = section;
        Text = text;
        SortOrder = sortOrder;
    }

    public int Id { get; private set; }
    public int QuoteVersionId { get; private set; }

    public TermSection Section { get; private set; }
    public string Text { get; private set; } = "";
    public int SortOrder { get; private set; }
}
