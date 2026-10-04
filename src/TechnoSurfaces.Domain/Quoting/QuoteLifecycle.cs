namespace TechnoSurfaces.Domain.Quoting;

/// <summary>
/// The things that can happen to a quote. There is deliberately no Reject: the
/// Managing Director corrects an estimator's quote and approves it (Task 1 5.2.1).
/// </summary>
public enum QuoteTransition
{
    Submit,
    Approve,
    Send,
    Accept,
    Reopen,
    Expire
}

/// <summary>
/// The quote lifecycle from Task 1 5.2.1, held as a table of legal moves. A move
/// that is not in the table cannot happen: <see cref="Next"/> throws rather than
/// returning the current status, so an illegal transition is never silent.
///
/// This class knows nothing about who is asking. Whether the person may approve is
/// an authorisation policy, and whether a Draft may skip PendingApproval (the
/// Managing Director approving their own quote) is decided by the workflow service.
/// </summary>
public static class QuoteLifecycle
{
    private static readonly IReadOnlyDictionary<(QuoteStatus From, QuoteTransition Via), QuoteStatus> Moves =
        new Dictionary<(QuoteStatus, QuoteTransition), QuoteStatus>
        {
            // An estimator's quote goes for approval.
            [(QuoteStatus.Draft, QuoteTransition.Submit)] = QuoteStatus.PendingApproval,

            // The Managing Director's own quote is approved directly; an estimator's
            // quote is approved from the queue, after any correction.
            [(QuoteStatus.Draft, QuoteTransition.Approve)] = QuoteStatus.Approved,
            [(QuoteStatus.PendingApproval, QuoteTransition.Approve)] = QuoteStatus.Approved,

            [(QuoteStatus.Approved, QuoteTransition.Send)] = QuoteStatus.Sent,
            [(QuoteStatus.Sent, QuoteTransition.Accept)] = QuoteStatus.Accepted,

            // A customer counter-offer. Reopening creates a new version.
            [(QuoteStatus.Sent, QuoteTransition.Reopen)] = QuoteStatus.Draft,
            [(QuoteStatus.Accepted, QuoteTransition.Reopen)] = QuoteStatus.Draft,

            // A customer who comes back after the quote lapsed: the quote is reopened
            // as a new version with a fresh validity period (team decision).
            [(QuoteStatus.Expired, QuoteTransition.Reopen)] = QuoteStatus.Draft,

            // The validity period lapsed with no answer. Task 1 5.2.1 also lets a
            // Draft that is never submitted expire.
            [(QuoteStatus.Sent, QuoteTransition.Expire)] = QuoteStatus.Expired,
            [(QuoteStatus.Draft, QuoteTransition.Expire)] = QuoteStatus.Expired,
        };

    public static bool Allows(QuoteStatus from, QuoteTransition via) => Moves.ContainsKey((from, via));

    public static QuoteStatus Next(QuoteStatus from, QuoteTransition via) =>
        Moves.TryGetValue((from, via), out var to) ? to : throw new InvalidQuoteTransitionException(from, via);
}

/// <summary>Raised for a move the lifecycle does not allow. The API answers it with 409.</summary>
public sealed class InvalidQuoteTransitionException : InvalidOperationException
{
    public InvalidQuoteTransitionException(QuoteStatus from, QuoteTransition via)
        : this(from, via, $"A quote that is {from} cannot be {Describe(via)}.")
    {
    }

    /// <summary>For a move the table allows in general but not in this case.</summary>
    public InvalidQuoteTransitionException(QuoteStatus from, QuoteTransition via, string message)
        : base(message)
    {
        From = from;
        Via = via;
    }

    public QuoteStatus From { get; }
    public QuoteTransition Via { get; }

    private static string Describe(QuoteTransition via) => via switch
    {
        QuoteTransition.Submit => "submitted for approval",
        QuoteTransition.Approve => "approved",
        QuoteTransition.Send => "sent",
        QuoteTransition.Accept => "accepted",
        QuoteTransition.Reopen => "reopened",
        QuoteTransition.Expire => "expired",
        _ => via.ToString()
    };
}
