using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.UnitTests.Quoting;

/// <summary>
/// The quote lifecycle from Task 1 5.2.1. Every legal move succeeds and every other
/// move is refused, so a quote can never reach a status by a route the client did
/// not describe.
/// </summary>
public sealed class QuoteLifecycleTests
{
    public static TheoryData<QuoteStatus, QuoteTransition, QuoteStatus> LegalMoves => new()
    {
        { QuoteStatus.Draft, QuoteTransition.Submit, QuoteStatus.PendingApproval },
        { QuoteStatus.Draft, QuoteTransition.Approve, QuoteStatus.Approved },
        { QuoteStatus.PendingApproval, QuoteTransition.Approve, QuoteStatus.Approved },
        { QuoteStatus.Approved, QuoteTransition.Send, QuoteStatus.Sent },
        { QuoteStatus.Sent, QuoteTransition.Accept, QuoteStatus.Accepted },
        { QuoteStatus.Sent, QuoteTransition.Reopen, QuoteStatus.Draft },
        { QuoteStatus.Accepted, QuoteTransition.Reopen, QuoteStatus.Draft },
        { QuoteStatus.Expired, QuoteTransition.Reopen, QuoteStatus.Draft },
        { QuoteStatus.Sent, QuoteTransition.Expire, QuoteStatus.Expired },
        { QuoteStatus.Draft, QuoteTransition.Expire, QuoteStatus.Expired },
    };

    [Theory]
    [MemberData(nameof(LegalMoves))]
    public void Every_legal_move_reaches_its_status(QuoteStatus from, QuoteTransition via, QuoteStatus to)
    {
        Assert.Equal(to, QuoteLifecycle.Next(from, via));
    }

    public static TheoryData<QuoteStatus, QuoteTransition> IllegalMoves()
    {
        var legal = LegalMoves.Select(row => ((QuoteStatus)row[0], (QuoteTransition)row[1])).ToHashSet();
        var data = new TheoryData<QuoteStatus, QuoteTransition>();
        foreach (var from in Enum.GetValues<QuoteStatus>())
            foreach (var via in Enum.GetValues<QuoteTransition>())
                if (!legal.Contains((from, via)))
                    data.Add(from, via);
        return data;
    }

    [Theory]
    [MemberData(nameof(IllegalMoves))]
    public void Every_other_move_is_refused(QuoteStatus from, QuoteTransition via)
    {
        var ex = Assert.Throws<InvalidQuoteTransitionException>(() => QuoteLifecycle.Next(from, via));
        Assert.Equal(from, ex.From);
        Assert.False(QuoteLifecycle.Allows(from, via));
    }

    [Fact]
    public void There_is_no_rejection_path()
    {
        Assert.DoesNotContain("Rejected", Enum.GetNames<QuoteStatus>());
        Assert.DoesNotContain("Reject", Enum.GetNames<QuoteTransition>());

        // Nothing leads from PendingApproval back to Draft.
        Assert.Throws<InvalidQuoteTransitionException>(() => QuoteLifecycle.Next(QuoteStatus.PendingApproval, QuoteTransition.Reopen));
    }

    [Fact]
    public void Approving_seals_the_version_that_was_approved()
    {
        var quote = new Quote("TS-1", 1, 1, "estimator", new DateOnly(2026, 10, 2));
        var version = quote.StartNewVersion("estimator", 40m);

        quote.Submit();
        quote.Approve("md");

        Assert.Equal(QuoteStatus.Approved, quote.Status);
        Assert.True(version.IsSealed);
        Assert.Equal("md", quote.ApprovedByUserId);
    }

    [Fact]
    public void The_managing_directors_own_draft_is_approved_directly()
    {
        var quote = new Quote("TS-5", 1, 1, "md", new DateOnly(2026, 10, 2));
        var version = quote.StartNewVersion("md", 40m);

        quote.Approve("md");

        Assert.Equal(QuoteStatus.Approved, quote.Status);
        Assert.True(version.IsSealed);
    }

    [Fact]
    public void An_estimators_draft_cannot_skip_the_approval_queue()
    {
        var quote = new Quote("TS-6", 1, 1, "estimator", new DateOnly(2026, 10, 2));
        var version = quote.StartNewVersion("estimator", 40m);

        var ex = Assert.Throws<InvalidQuoteTransitionException>(() => quote.Approve("md"));

        Assert.Equal(QuoteStatus.Draft, ex.From);
        Assert.Equal(QuoteStatus.Draft, quote.Status);
        Assert.False(version.IsSealed);
        Assert.Null(quote.ApprovedByUserId);
    }

    [Fact]
    public void A_quote_with_no_version_cannot_be_approved()
    {
        var quote = new Quote("TS-7", 1, 1, "md", new DateOnly(2026, 10, 2));

        Assert.Throws<InvalidOperationException>(() => quote.Approve("md"));
        Assert.Equal(QuoteStatus.Draft, quote.Status);
        Assert.Null(quote.ApprovedByUserId);
    }

    [Fact]
    public void A_pending_quote_stays_open_for_correction_until_approved()
    {
        var quote = new Quote("TS-4", 1, 1, "estimator", new DateOnly(2026, 10, 2));
        var version = quote.StartNewVersion("estimator", 40m);

        quote.Submit();
        version.SetMarkupPercent(45m);

        Assert.Equal(QuoteStatus.PendingApproval, quote.Status);
        Assert.False(version.IsSealed);
        Assert.Equal(45m, version.MarkupPercent);
    }

    [Fact]
    public void A_quote_expires_only_after_its_validity_period()
    {
        var quote = new Quote("TS-2", 1, 1, "md", new DateOnly(2026, 10, 2), validForDays: 30);
        quote.StartNewVersion("md", 40m);
        quote.Approve("md");
        quote.MarkSent();

        Assert.False(quote.ExpireIfLapsed(quote.ValidUntil));
        Assert.Equal(QuoteStatus.Sent, quote.Status);

        Assert.True(quote.ExpireIfLapsed(quote.ValidUntil.AddDays(1)));
        Assert.Equal(QuoteStatus.Expired, quote.Status);
    }

    [Fact]
    public void An_accepted_quote_does_not_expire()
    {
        var quote = new Quote("TS-3", 1, 1, "md", new DateOnly(2026, 10, 2));
        quote.StartNewVersion("md", 40m);
        quote.Approve("md");
        quote.MarkSent();
        quote.MarkAccepted();

        Assert.False(quote.ExpireIfLapsed(quote.ValidUntil.AddDays(60)));
        Assert.Equal(QuoteStatus.Accepted, quote.Status);
    }
}
