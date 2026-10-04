using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.UnitTests.Quoting;

/// <summary>
/// The job details printed on the quotation (US-14) can be changed while the quote
/// is being prepared and checked, and are fixed once it is approved, so an issued
/// quotation cannot be altered after the fact (US-21).
/// </summary>
public sealed class QuoteDetailsTests
{
    private const string Estimator = "estimator";
    private const string ManagingDirector = "managing-director";

    private static Quote DraftBy(string author)
    {
        var quote = new Quote("TS-DETAILS-1", customerId: 1, contactId: 1, author, new DateOnly(2026, 10, 3));
        quote.StartNewVersion(author, markupPercent: 47m);
        quote.UpdateDetails("Claremont", "Kitchen", "SWEET VALLEY FARM", "12 Main Road");
        return quote;
    }

    private static Quote ApprovedByTheManagingDirector()
    {
        var quote = DraftBy(ManagingDirector);
        quote.Approve(ManagingDirector);
        return quote;
    }

    [Fact]
    public void Details_can_be_set_on_a_draft()
    {
        var quote = DraftBy(Estimator);

        Assert.Equal("Claremont", quote.Site);
        Assert.Equal("Kitchen", quote.Project);
        Assert.Equal("SWEET VALLEY FARM", quote.CustomerReference);
        Assert.Equal("12 Main Road", quote.DeliveryAddress);
    }

    [Fact]
    public void The_managing_director_can_correct_details_on_a_pending_quote()
    {
        var quote = DraftBy(Estimator);
        quote.Submit();

        quote.UpdateDetails("Constantia", "Kitchen", "SWEET VALLEY FARM", "12 Main Road");

        Assert.Equal(QuoteStatus.PendingApproval, quote.Status);
        Assert.Equal("Constantia", quote.Site);
    }

    [Fact]
    public void Details_cannot_be_changed_once_approved()
    {
        var quote = ApprovedByTheManagingDirector();

        Assert.Throws<InvalidOperationException>(() =>
            quote.UpdateDetails("Constantia", "Kitchen", "SWEET VALLEY FARM", "12 Main Road"));
        Assert.Equal("Claremont", quote.Site);
    }

    [Fact]
    public void Details_cannot_be_changed_once_sent()
    {
        var quote = ApprovedByTheManagingDirector();
        quote.MarkSent();

        Assert.Throws<InvalidOperationException>(() =>
            quote.UpdateDetails("Claremont", "Bathroom", "SWEET VALLEY FARM", "12 Main Road"));
        Assert.Equal("Kitchen", quote.Project);
    }

    [Fact]
    public void Details_cannot_be_changed_once_accepted()
    {
        var quote = ApprovedByTheManagingDirector();
        quote.MarkSent();
        quote.MarkAccepted();

        Assert.Throws<InvalidOperationException>(() =>
            quote.UpdateDetails("Claremont", "Kitchen", "ANOTHER REF", "12 Main Road"));
        Assert.Equal("SWEET VALLEY FARM", quote.CustomerReference);
    }

    [Fact]
    public void Details_cannot_be_changed_once_expired()
    {
        var quote = DraftBy(Estimator);
        Assert.True(quote.ExpireIfLapsed(quote.ValidUntil.AddDays(1)));

        Assert.Throws<InvalidOperationException>(() =>
            quote.UpdateDetails("Claremont", "Kitchen", "SWEET VALLEY FARM", "1 Other Road"));
        Assert.Equal("12 Main Road", quote.DeliveryAddress);
    }
}
