using TechnoSurfaces.Application.Auditing;
using TechnoSurfaces.Application.Customers;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.Application.Quoting;

/// <summary>
/// The quote workflow (Task 1 5.2.1): create, submit, approve, send, accept and
/// reopen, with the lists and the approval queue the screens show.
///
/// Who may do each step is an authorisation policy, checked by the caller before it
/// calls in. This service does not test roles:
/// <list type="bullet">
/// <item>Create, list, read, mark sent: any signed-in user.</item>
/// <item>Update details, submit: CanEditQuote, with the quote as the resource.</item>
/// <item>Approve, mark accepted, the approval queue: CanApproveQuote.</item>
/// <item>Reopen: CanReopenQuote, with the quote as the resource.</item>
/// </list>
/// The signed-in user comes from <see cref="ICurrentUser"/>; today's date from the
/// <see cref="TimeProvider"/>, in South African time.
/// </summary>
public interface IQuoteWorkflowService
{
    /// <summary>The quote list. Lapsed quotes are expired first, so the list shows the right status.</summary>
    Task<IReadOnlyList<QuoteSummary>> ListAsync(QuoteListFilter filter, CancellationToken ct = default);

    /// <summary>The Managing Director's approval queue (US-17).</summary>
    Task<IReadOnlyList<QuoteSummary>> ApprovalQueueAsync(CancellationToken ct = default);

    /// <summary>How many quotes are waiting for approval, for the side menu.</summary>
    Task<int> PendingCountAsync(CancellationToken ct = default);

    Task<WorkflowResult> GetAsync(int quoteId, CancellationToken ct = default);

    /// <summary>Every version of a quote with its date, author and total (US-21). Null for an unknown quote.</summary>
    Task<IReadOnlyList<QuoteVersionSummary>?> VersionsAsync(int quoteId, CancellationToken ct = default);

    Task<WorkflowResult> CreateAsync(NewQuote input, CancellationToken ct = default);

    Task<WorkflowResult> UpdateDetailsAsync(int quoteId, QuoteDetailsInput input, CancellationToken ct = default);

    /// <summary>An estimator's quote goes to the approval queue (US-16).</summary>
    Task<WorkflowResult> SubmitAsync(int quoteId, CancellationToken ct = default);

    /// <summary>
    /// Approves a pending quote, or the Managing Director's own draft. Records the
    /// standing terms and warranties on the version in the same save, then seals it.
    /// Corrections (US-18) are made through the costing sheet and details before this.
    /// </summary>
    Task<WorkflowResult> ApproveAsync(int quoteId, CancellationToken ct = default);

    Task<WorkflowResult> MarkSentAsync(int quoteId, CancellationToken ct = default);

    /// <summary>Records that the customer signed and returned the quotation.</summary>
    Task<WorkflowResult> MarkAcceptedAsync(int quoteId, CancellationToken ct = default);

    /// <summary>A new version after a counter-offer, or after the quote lapsed (US-20).</summary>
    Task<WorkflowResult> ReopenAsync(int quoteId, CancellationToken ct = default);

    /// <summary>Moves every lapsed Draft and Sent quote to Expired. Returns how many changed.</summary>
    Task<int> ExpireLapsedAsync(CancellationToken ct = default);
}

public sealed class QuoteWorkflowService : IQuoteWorkflowService
{
    private readonly IQuoteRepository _quotes;
    private readonly IQuoteQueries _queries;
    private readonly ILapsedQuotes _lapsed;
    private readonly ICustomerRepository _customers;
    private readonly IQuoteTermsRecorder _terms;
    private readonly ICurrentUser _user;
    private readonly TimeProvider _time;

    public QuoteWorkflowService(
        IQuoteRepository quotes,
        IQuoteQueries queries,
        ILapsedQuotes lapsed,
        ICustomerRepository customers,
        IQuoteTermsRecorder terms,
        ICurrentUser user,
        TimeProvider time)
    {
        _quotes = quotes;
        _queries = queries;
        _lapsed = lapsed;
        _customers = customers;
        _terms = terms;
        _user = user;
        _time = time;
    }

    private DateOnly Today => BusinessDate.Today(_time);

    // ---- Reading ----

    public async Task<IReadOnlyList<QuoteSummary>> ListAsync(QuoteListFilter filter, CancellationToken ct = default)
    {
        await ExpireLapsedAsync(ct);
        return await _queries.ListAsync(filter, Today, ct);
    }

    public Task<IReadOnlyList<QuoteSummary>> ApprovalQueueAsync(CancellationToken ct = default) =>
        _queries.ApprovalQueueAsync(Today, ct);

    public Task<int> PendingCountAsync(CancellationToken ct = default) => _queries.PendingCountAsync(ct);

    public async Task<WorkflowResult> GetAsync(int quoteId, CancellationToken ct = default)
    {
        await ExpireLapsedAsync(ct);
        return await Detail(quoteId, ct);
    }

    public Task<IReadOnlyList<QuoteVersionSummary>?> VersionsAsync(int quoteId, CancellationToken ct = default) =>
        _queries.VersionsAsync(quoteId, ct);

    // ---- Creating and editing ----

    public async Task<WorkflowResult> CreateAsync(NewQuote input, CancellationToken ct = default)
    {
        var reference = Clean(input.Reference) ?? "";
        var details = Normalise(new QuoteDetailsInput(input.Site, input.Project, input.CustomerReference, input.DeliveryAddress));

        var errors = new Dictionary<string, string[]>();
        if (reference.Length == 0)
            errors[nameof(NewQuote.Reference)] = new[] { "A quote needs a reference." };
        else if (reference.Length > 40)
            errors[nameof(NewQuote.Reference)] = new[] { "No more than 40 characters." };
        if (input.MarkupPercent is < 0 or > 999.99m)
            errors[nameof(NewQuote.MarkupPercent)] = new[] { "A markup must be between 0 and 999.99 per cent." };
        ValidateDetails(details, errors);

        var customer = await _customers.GetAsync(input.CustomerId, ct);
        if (customer is not { IsActive: true })
            errors[nameof(NewQuote.CustomerId)] = new[] { "Choose an active customer." };
        else if (customer.Contacts.FirstOrDefault(c => c.Id == input.ContactId) is not { IsActive: true })
            errors[nameof(NewQuote.ContactId)] = new[] { "Choose an active contact at this customer." };

        if (errors.Count > 0)
            return new(WorkflowOutcome.Invalid, Errors: errors);

        if (await _quotes.ReferenceExistsAsync(reference, ct))
            return new(WorkflowOutcome.ReferenceTaken, Problem: $"Another quote already has the reference {reference}.");

        var quote = new Quote(reference, input.CustomerId, input.ContactId, _user.UserId, Today);
        quote.UpdateDetails(details.Site, details.Project, details.CustomerReference, details.DeliveryAddress);
        quote.StartNewVersion(_user.UserId, input.MarkupPercent);

        _quotes.Add(quote);
        await _quotes.SaveChangesAsync(ct);
        return await Detail(quote.Id, ct);
    }

    public async Task<WorkflowResult> UpdateDetailsAsync(int quoteId, QuoteDetailsInput input, CancellationToken ct = default)
    {
        var quote = await _quotes.GetAsync(quoteId, ct);
        if (quote is null)
            return new(WorkflowOutcome.NotFound);

        var details = Normalise(input);
        var errors = new Dictionary<string, string[]>();
        ValidateDetails(details, errors);
        if (errors.Count > 0)
            return new(WorkflowOutcome.Invalid, Errors: errors);

        if (quote.Status is not (QuoteStatus.Draft or QuoteStatus.PendingApproval))
            return NotAllowed($"A quote that is {quote.Status} cannot be changed. Reopen it to make a revision.");

        quote.UpdateDetails(details.Site, details.Project, details.CustomerReference, details.DeliveryAddress);
        await _quotes.SaveChangesAsync(ct);
        return await Detail(quoteId, ct);
    }

    // ---- The lifecycle ----

    public Task<WorkflowResult> SubmitAsync(int quoteId, CancellationToken ct = default) =>
        StepAsync(quoteId, QuoteTransition.Submit, quote =>
        {
            if (quote.CurrentVersion?.CostingLines.Count is not > 0)
                return Task.FromResult<string?>("Add at least one costing line before submitting the quote.");
            quote.Submit();
            return Task.FromResult<string?>(null);
        }, ct);

    public Task<WorkflowResult> ApproveAsync(int quoteId, CancellationToken ct = default) =>
        StepAsync(quoteId, QuoteTransition.Approve, async quote =>
        {
            if (quote.Status == QuoteStatus.Draft && quote.CreatedByUserId != _user.UserId)
                return "A draft can be approved directly only by its author. Submit it for approval first.";
            if (quote.CurrentVersion?.CostingLines.Count is not > 0)
                return "A quote with no costing lines has nothing to approve.";

            // The terms and warranties the version is issued with, recorded in the
            // same save as the approval (US-21, US-22).
            try
            {
                await _terms.RecordAsync(quote.CurrentVersion!, ct);
            }
            catch (ArgumentException)
            {
                return "No standing terms are set, so the quotation cannot be issued. The Managing Director sets them on the quotation terms screen.";
            }

            quote.Approve(_user.UserId);
            return null;
        }, ct);

    public Task<WorkflowResult> MarkSentAsync(int quoteId, CancellationToken ct = default) =>
        StepAsync(quoteId, QuoteTransition.Send, quote =>
        {
            quote.MarkSent();
            return Task.FromResult<string?>(null);
        }, ct);

    public Task<WorkflowResult> MarkAcceptedAsync(int quoteId, CancellationToken ct = default) =>
        StepAsync(quoteId, QuoteTransition.Accept, quote =>
        {
            quote.MarkAccepted();
            return Task.FromResult<string?>(null);
        }, ct);

    public Task<WorkflowResult> ReopenAsync(int quoteId, CancellationToken ct = default) =>
        StepAsync(quoteId, QuoteTransition.Reopen, quote =>
        {
            quote.Reopen(_user.UserId, Today);
            return Task.FromResult<string?>(null);
        }, ct);

    public async Task<int> ExpireLapsedAsync(CancellationToken ct = default)
    {
        var today = Today;
        var expired = 0;
        foreach (var quote in await _lapsed.FindAsync(today, ct))
            if (quote.ExpireIfLapsed(today))
                expired++;

        if (expired > 0)
            await _quotes.SaveChangesAsync(ct);
        return expired;
    }

    /// <summary>
    /// Loads the quote, checks the lifecycle allows the step, applies it and saves.
    /// <paramref name="apply"/> returns a reason to refuse, or null once it has made
    /// the change. Nothing is saved when the step is refused.
    /// </summary>
    private async Task<WorkflowResult> StepAsync(
        int quoteId, QuoteTransition via, Func<Quote, Task<string?>> apply, CancellationToken ct)
    {
        var quote = await _quotes.GetAsync(quoteId, ct);
        if (quote is null)
            return new(WorkflowOutcome.NotFound);

        // A quote that has lapsed is expired before the step is considered, so a
        // lapsed quote cannot be sent or accepted. The expiry is saved even when the
        // step is then refused, because it happened either way.
        var expired = quote.ExpireIfLapsed(Today);

        if (!QuoteLifecycle.Allows(quote.Status, via))
        {
            if (expired)
            {
                await _quotes.SaveChangesAsync(ct);
                return NotAllowed($"This quote expired after {quote.ValidUntil:yyyy-MM-dd}. Reopen it to make a new version.");
            }

            return NotAllowed(new InvalidQuoteTransitionException(quote.Status, via).Message);
        }

        string? refusal;
        try
        {
            refusal = await apply(quote);
        }
        catch (InvalidQuoteTransitionException ex)
        {
            refusal = ex.Message;
        }

        if (refusal is not null)
            return NotAllowed(refusal);

        await _quotes.SaveChangesAsync(ct);
        return await Detail(quoteId, ct);
    }

    private async Task<WorkflowResult> Detail(int quoteId, CancellationToken ct)
    {
        var detail = await _queries.GetAsync(quoteId, Today, ct);
        return detail is null ? new(WorkflowOutcome.NotFound) : new(WorkflowOutcome.Ok, detail);
    }

    private static WorkflowResult NotAllowed(string problem) => new(WorkflowOutcome.NotAllowed, Problem: problem);

    // ---- Input rules. Lengths match the columns in QuoteConfiguration. ----

    private static QuoteDetailsInput Normalise(QuoteDetailsInput i) =>
        new(Clean(i.Site), Clean(i.Project), Clean(i.CustomerReference), Clean(i.DeliveryAddress));

    private static void ValidateDetails(QuoteDetailsInput d, Dictionary<string, string[]> errors)
    {
        MaxLength(errors, nameof(QuoteDetailsInput.Site), d.Site, 200);
        MaxLength(errors, nameof(QuoteDetailsInput.Project), d.Project, 200);
        MaxLength(errors, nameof(QuoteDetailsInput.CustomerReference), d.CustomerReference, 100);
        MaxLength(errors, nameof(QuoteDetailsInput.DeliveryAddress), d.DeliveryAddress, 300);
    }

    private static void MaxLength(Dictionary<string, string[]> errors, string field, string? value, int max)
    {
        if (value is not null && value.Length > max)
            errors[field] = new[] { $"No more than {max} characters." };
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
