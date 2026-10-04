using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.UnitTests.Quoting;

/// <summary>
/// The Memento pattern (Task 1 7.1.2): reopening a sent quote after a counter-offer
/// creates a new version that starts as a copy, and leaves the original untouched
/// (US-20, US-21, US-22).
/// </summary>
public sealed class QuoteRevisionTests
{
    private readonly QuoteCalculationService _calculator = new();

    private static readonly DateOnly ReopenedOn = new(2026, 10, 25);

    private static Quote SentQuoteWithLines(out QuoteVersion first)
    {
        var quote = new Quote("TS-REV-1", 1, 1, "estimator", new DateOnly(2026, 10, 2));
        first = quote.StartNewVersion("estimator", markupPercent: 40m);

        var material = CostingLine.ForMaterial(7, "Staron Bright White 3680 x 760", 4335.04m,
            "Staron (Salvocorp) price band Bright White, 3680 x 760, effective 2025-03-01", 2m, 2.7968m, 5m);
        var labour = CostingLine.ForRate(1, "Fabrication — no backsplash, normal", 265m, "Rate card", 6m, isBelowTheLine: false);
        var silicon = CostingLine.ForRate(2, "Silicon + sealing", 55m, "Rate card", 0m, isBelowTheLine: false,
            DerivationRule.FromSheetCount, derivationFactor: 2m);

        first.AddCostingLine(material);
        first.AddCostingLine(labour);
        first.AddCostingLine(silicon);
        first.OverrideUnitPrice(labour, 300m);
        first.ChangeQuantity(silicon, 7m);
        first.SetTransportAmount(850m);
        first.AddQuotationLine(new QuotationLine("Kitchen countertop, fabricate and install", 10000m, "Kitchen"));

        quote.Submit();
        quote.Approve("md");
        quote.MarkSent();
        return quote;
    }

    [Fact]
    public void Reopening_creates_version_two_and_returns_the_quote_to_draft()
    {
        var quote = SentQuoteWithLines(out var first);

        var second = quote.Reopen("estimator", ReopenedOn);

        Assert.Equal(QuoteStatus.Draft, quote.Status);
        Assert.Equal(2, quote.Versions.Count);
        Assert.Equal(2, second.VersionNo);
        Assert.Same(second, quote.CurrentVersion);
        Assert.Same(first, quote.OriginalVersion);
        Assert.False(second.IsSealed);
        Assert.True(first.IsSealed);
    }

    [Fact]
    public void The_revision_starts_as_an_exact_copy_with_the_same_prices()
    {
        var quote = SentQuoteWithLines(out var first);

        var second = quote.Reopen("estimator", ReopenedOn);

        Assert.Equal(_calculator.Calculate(first), _calculator.Calculate(second));
        Assert.Equal(first.MarkupPercent, second.MarkupPercent);
        Assert.Equal(first.TransportAmount, second.TransportAmount);
        Assert.Equal(first.QuotationLines.Single().AmountExVat, second.QuotationLines.Single().AmountExVat);

        foreach (var (was, copy) in first.CostingLines.Zip(second.CostingLines))
        {
            Assert.NotSame(was, copy);
            Assert.Equal(was.MaterialPriceId, copy.MaterialPriceId);
            Assert.Equal(was.RateItemId, copy.RateItemId);
            Assert.Equal(was.ResolvedUnitPrice, copy.ResolvedUnitPrice);
            Assert.Equal(was.OverriddenUnitPrice, copy.OverriddenUnitPrice);
            Assert.Equal(was.PriceOrigin, copy.PriceOrigin);
            Assert.Equal(was.Quantity, copy.Quantity);
            Assert.Equal(was.IsQuantityOverridden, copy.IsQuantityOverridden);
            Assert.Equal(was.SupplierDiscountPercent, copy.SupplierDiscountPercent);
        }
    }

    [Fact]
    public void Editing_the_revision_leaves_version_one_unchanged()
    {
        var quote = SentQuoteWithLines(out var first);
        var totalsBefore = _calculator.Calculate(first);

        var second = quote.Reopen("estimator", ReopenedOn);
        second.SetMarkupPercent(30m);
        second.ChangeQuantity(second.CostingLines.First(), 3m);

        Assert.Equal(totalsBefore, _calculator.Calculate(first));
        Assert.NotEqual(totalsBefore, _calculator.Calculate(second));
        Assert.Throws<InvalidOperationException>(() => first.SetMarkupPercent(30m));
    }

    [Fact]
    public void An_accepted_quote_can_also_be_reopened()
    {
        var quote = SentQuoteWithLines(out var first);
        quote.MarkAccepted();

        var second = quote.Reopen("md", ReopenedOn);

        Assert.Equal(QuoteStatus.Draft, quote.Status);
        Assert.Equal(2, second.VersionNo);
        Assert.Equal(_calculator.Calculate(first), _calculator.Calculate(second));
    }

    [Fact]
    public void A_reopened_quote_is_no_longer_shown_as_approved()
    {
        var quote = SentQuoteWithLines(out _);
        Assert.Equal("md", quote.ApprovedByUserId);

        quote.Reopen("estimator", ReopenedOn);

        Assert.Null(quote.ApprovedByUserId);
        Assert.Null(quote.ApprovedAtUtc);
    }

    [Fact]
    public void Reopening_starts_a_fresh_validity_period_and_keeps_the_issue_date()
    {
        var quote = SentQuoteWithLines(out _);
        var issued = quote.IssueDate;

        quote.Reopen("estimator", ReopenedOn);

        Assert.Equal(ReopenedOn.AddDays(Quote.DefaultValidForDays), quote.ValidUntil);
        Assert.Equal(issued, quote.IssueDate);
        Assert.False(quote.ExpireIfLapsed(ReopenedOn.AddDays(Quote.DefaultValidForDays)));
    }

    [Fact]
    public void An_expired_quote_can_be_reopened_as_a_new_version()
    {
        var quote = SentQuoteWithLines(out _);
        Assert.True(quote.ExpireIfLapsed(quote.ValidUntil.AddDays(1)));

        var revision = quote.Reopen("estimator", ReopenedOn);

        Assert.Equal(QuoteStatus.Draft, quote.Status);
        Assert.Equal(2, revision.VersionNo);
        Assert.Equal(ReopenedOn.AddDays(Quote.DefaultValidForDays), quote.ValidUntil);
    }

    [Fact]
    public void A_draft_cannot_be_reopened()
    {
        var quote = new Quote("TS-REV-2", 1, 1, "estimator", new DateOnly(2026, 10, 2));
        quote.StartNewVersion("estimator", 40m);

        Assert.Throws<InvalidQuoteTransitionException>(() => quote.Reopen("estimator", ReopenedOn));
        Assert.Single(quote.Versions);
    }
}
