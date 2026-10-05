using TechnoSurfaces.Domain.People;

namespace TechnoSurfaces.Domain.Quoting;

/// <summary>
/// A priced offer for a single job. Holds the identity of the job, its reference,
/// site, project and period of validity, together with the commercial terms under
/// which it is offered.
///
/// The originator in the Memento pattern: a quote owns an ordered series of
/// immutable <see cref="QuoteVersion"/> snapshots. Reopening a quote after a
/// customer counter-offer produces a new version and leaves the earlier one intact.
/// </summary>
public class Quote
{
    private readonly List<QuoteVersion> _versions = new();

    private Quote() { }

    /// <summary>"Quotation valid for 30 days only", from the client's standing terms.</summary>
    public const int DefaultValidForDays = 30;

    public Quote(string reference, int customerId, int contactId, string createdByUserId, DateOnly issueDate, int validForDays = DefaultValidForDays)
    {
        if (string.IsNullOrWhiteSpace(reference))
            throw new ArgumentException("A quote needs a reference.", nameof(reference));

        Reference = reference;
        CustomerId = customerId;
        ContactId = contactId;
        CreatedByUserId = createdByUserId;
        IssueDate = issueDate;
        ValidUntil = issueDate.AddDays(validForDays);
        Status = QuoteStatus.Draft;
        CreatedAtUtc = DateTime.UtcNow;
    }

    public int Id { get; private set; }

    /// <summary>Unique. One reference identifies one quote.</summary>
    public string Reference { get; private set; } = "";

    public int CustomerId { get; private set; }
    public Customer? Customer { get; private set; }

    /// <summary>The quotation is addressed to a named person.</summary>
    public int ContactId { get; private set; }
    public Contact? Contact { get; private set; }

    // The job details below are printed on the customer quotation (US-14). They
    // change only through UpdateDetails, which refuses once the quote has been
    // approved, so an issued quotation cannot be altered after the fact (US-21).

    public string? Site { get; private set; }
    public string? Project { get; private set; }

    /// <summary>
    /// The customer's own reference for the job, printed as "Your ref" on the
    /// quotation and carried to the Pastel invoice.
    /// </summary>
    public string? CustomerReference { get; private set; }

    public string? DeliveryAddress { get; private set; }

    public DateOnly IssueDate { get; private set; }
    public DateOnly ValidUntil { get; private set; }

    public QuoteStatus Status { get; private set; }

    public string CreatedByUserId { get; private set; } = "";
    public DateTime CreatedAtUtc { get; private set; }

    public string? ApprovedByUserId { get; private set; }
    public DateTime? ApprovedAtUtc { get; private set; }

    public IReadOnlyCollection<QuoteVersion> Versions => _versions.AsReadOnly();

    /// <summary>
    /// Sets the job details printed on the quotation. Allowed while the quote is a
    /// draft, and while it is pending approval so the Managing Director can correct
    /// it before approving (US-18). Refused once it is approved: what the customer
    /// was sent stays as it was, and a change needs the quote reopened as a new
    /// version.
    /// </summary>
    public void UpdateDetails(string? site, string? project, string? customerReference, string? deliveryAddress)
    {
        if (Status is not (QuoteStatus.Draft or QuoteStatus.PendingApproval))
            throw new InvalidOperationException(
                $"A quote that is {Status} cannot be changed. Reopen it to make a revision.");

        Site = site;
        Project = project;
        CustomerReference = customerReference;
        DeliveryAddress = deliveryAddress;
    }

    /// <summary>The version currently being worked on or last issued.</summary>
    public QuoteVersion? CurrentVersion => _versions.OrderByDescending(v => v.VersionNo).FirstOrDefault();

    /// <summary>The original offer, retained so that it can be compared with a revision.</summary>
    public QuoteVersion? OriginalVersion => _versions.OrderBy(v => v.VersionNo).FirstOrDefault();

    /// <summary>Any version of the quote by its number, or null when there is no such version.</summary>
    public QuoteVersion? Version(int versionNo) => _versions.FirstOrDefault(v => v.VersionNo == versionNo);

    /// <summary>
    /// Starts a new snapshot. The previous version is sealed first, so earlier
    /// versions can never be altered by a later revision.
    /// </summary>
    public QuoteVersion StartNewVersion(string createdByUserId, decimal markupPercent)
    {
        CurrentVersion?.Seal();
        var next = new QuoteVersion((CurrentVersion?.VersionNo ?? 0) + 1, createdByUserId, markupPercent);
        _versions.Add(next);
        return next;
    }

    // ---- The lifecycle (Task 1 5.2.1). Status has no public setter; every change
    // ---- goes through QuoteLifecycle, which refuses an illegal move.

    /// <summary>An estimator's quote goes to the approval queue (US-16).</summary>
    public void Submit() => Status = QuoteLifecycle.Next(Status, QuoteTransition.Submit);

    /// <summary>
    /// Approves the quote and seals its current version, so what was approved is
    /// what is sent. Any correction by the Managing Director (US-18) is made before
    /// this call, while the version is still open.
    ///
    /// A Draft may be approved directly only by its own author: that is the
    /// Managing Director approving their own quote. Anyone else's Draft must be
    /// submitted and approved from the queue (Task 1 5.2.1). Who may approve at all
    /// is the CanApproveQuote policy, so an estimator never reaches this call.
    /// </summary>
    public void Approve(string approvedByUserId)
    {
        var version = CurrentVersion
            ?? throw new InvalidOperationException("A quote with no version has nothing to approve.");

        var next = QuoteLifecycle.Next(Status, QuoteTransition.Approve);

        if (Status == QuoteStatus.Draft && approvedByUserId != CreatedByUserId)
            throw new InvalidQuoteTransitionException(Status, QuoteTransition.Approve,
                "A draft can be approved directly only by its author. Submit it for approval first.");

        Status = next;
        version.RecordIssue(new IssuedHeading(
            Contact?.FullName ?? "", Customer?.Name ?? "", Contact?.Phone, Contact?.Email,
            Site, Project, CustomerReference, ValidUntil), approvedByUserId);
        version.Seal();
        ApprovedByUserId = approvedByUserId;
        ApprovedAtUtc = DateTime.UtcNow;
    }

    public void MarkSent() => Status = QuoteLifecycle.Next(Status, QuoteTransition.Send);

    /// <summary>Records that the signed acceptance came back. The signature is not captured.</summary>
    public void MarkAccepted() => Status = QuoteLifecycle.Next(Status, QuoteTransition.Accept);

    /// <summary>
    /// Reopens a sent or accepted quote after a counter-offer (US-20). This is the
    /// Memento step: the current version is sealed and left as it is, and a new
    /// version starts as a copy of it, with every line keeping the price it was
    /// created with (US-21, US-22). The quote goes back to Draft.
    ///
    /// The earlier approval is cleared, because it approved the earlier version and
    /// not this revision. The sealed version and the audit trail keep the record of
    /// who approved what.
    ///
    /// The validity period starts again from <paramref name="reopenedOn"/>, so the
    /// revision is not issued already lapsed or about to lapse (team decision). An
    /// expired quote can be reopened the same way. The issue date does not move:
    /// prices are resolved as at the issue date, and the original offer keeps it.
    /// </summary>
    public QuoteVersion Reopen(string reopenedByUserId, DateOnly reopenedOn, int validForDays = DefaultValidForDays)
    {
        var current = CurrentVersion
            ?? throw new InvalidOperationException("A quote with no version cannot be reopened.");
        if (validForDays < 1)
            throw new ArgumentOutOfRangeException(nameof(validForDays), "A quote must be valid for at least one day.");

        Status = QuoteLifecycle.Next(Status, QuoteTransition.Reopen);
        current.Seal();
        ApprovedByUserId = null;
        ApprovedAtUtc = null;
        ValidUntil = reopenedOn.AddDays(validForDays);

        var next = current.CreateRevision(current.VersionNo + 1, reopenedByUserId);
        _versions.Add(next);
        return next;
    }

    /// <summary>
    /// Moves the quote to Expired once its validity period has passed. Returns
    /// false, and changes nothing, while the quote is still valid on
    /// <paramref name="today"/> or is in a status that cannot expire.
    /// </summary>
    public bool ExpireIfLapsed(DateOnly today)
    {
        if (today <= ValidUntil || !QuoteLifecycle.Allows(Status, QuoteTransition.Expire))
            return false;

        Status = QuoteLifecycle.Next(Status, QuoteTransition.Expire);
        return true;
    }
}
