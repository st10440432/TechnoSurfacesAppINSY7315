using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TechnoSurfaces.Application.Auditing;
using TechnoSurfaces.Application.Quoting;
using TechnoSurfacesApp.Identity;

namespace TechnoSurfacesApp.Api;

/// <summary>
/// The quote workflow over HTTP (US-14 to US-18, US-20, US-21): create, list, the
/// approval queue, submit, approve, send, accept and reopen.
///
/// Each step is checked against its policy before the service runs, and a refusal
/// is 403 with ProblemDetails. A step the quote's status does not allow is 409. The
/// rules, in one place:
/// <list type="bullet">
/// <item>Create, list, read, version history, mark sent: any signed-in user.</item>
/// <item>Change details, submit: CanEditQuote (MD any quote, estimator own draft).</item>
/// <item>Approval queue, approve, mark accepted: CanApproveQuote (MD only).</item>
/// <item>Reopen: CanReopenQuote (MD any quote, estimator own quote).</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/quotes")]
public sealed class QuoteWorkflowController : ControllerBase
{
    private readonly IQuoteWorkflowService _workflow;
    private readonly IQuoteRepository _quotes;
    private readonly IAuthorizationService _authorization;
    private readonly ICurrentUser _user;

    public QuoteWorkflowController(
        IQuoteWorkflowService workflow, IQuoteRepository quotes, IAuthorizationService authorization, ICurrentUser user)
    {
        _workflow = workflow;
        _quotes = quotes;
        _authorization = authorization;
        _user = user;
    }

    /// <summary>GET /api/quotes?status=&amp;customerId=&amp;mine=&amp;search=</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<QuoteSummary>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(string? status, int? customerId, bool mine, string? search, CancellationToken ct) =>
        Ok(await _workflow.ListAsync(new QuoteListFilter(status, customerId, mine ? _user.UserId : null, search), ct));

    /// <summary>GET /api/quotes/approval-queue (US-17). Managing Director only.</summary>
    [HttpGet("approval-queue")]
    [ProducesResponseType<IReadOnlyList<QuoteSummary>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ApprovalQueue(CancellationToken ct)
    {
        if (await RefuseUnlessAsync(Policies.CanApproveQuote, "Only the Managing Director works the approval queue.") is { } refused)
            return refused;
        return Ok(await _workflow.ApprovalQueueAsync(ct));
    }

    /// <summary>GET /api/quotes/pending-count: the number on the side menu.</summary>
    [HttpGet("pending-count")]
    [ProducesResponseType<int>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PendingCount(CancellationToken ct) =>
        Ok(await _workflow.PendingCountAsync(ct));

    /// <summary>GET /api/quotes/{id}</summary>
    [HttpGet("{id:int}", Name = nameof(GetQuote))]
    [ProducesResponseType<QuoteDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetQuote(int id, CancellationToken ct) =>
        Respond(await _workflow.GetAsync(id, ct), id);

    /// <summary>GET /api/quotes/{id}/versions (US-21)</summary>
    [HttpGet("{id:int}/versions")]
    [ProducesResponseType<IReadOnlyList<QuoteVersionSummary>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Versions(int id, CancellationToken ct) =>
        await _workflow.VersionsAsync(id, ct) is { } versions ? Ok(versions) : NotFoundProblem(id);

    /// <summary>POST /api/quotes (US-14, US-15)</summary>
    [HttpPost]
    [ProducesResponseType<QuoteDetail>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreateQuoteRequest request, CancellationToken ct)
    {
        var result = await _workflow.CreateAsync(new NewQuote(
            request.Reference, request.CustomerId!.Value, request.ContactId!.Value, request.MarkupPercent!.Value,
            request.Site, request.Project, request.CustomerReference, request.DeliveryAddress), ct);

        return result.Outcome == WorkflowOutcome.Ok
            ? CreatedAtRoute(nameof(GetQuote), new { id = result.Quote!.Id }, result.Quote)
            : Respond(result, 0);
    }

    /// <summary>PUT /api/quotes/{id}/details: site, project, your ref and delivery address.</summary>
    [HttpPut("{id:int}/details")]
    [ProducesResponseType<QuoteDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateDetails(int id, QuoteDetailsRequest request, CancellationToken ct)
    {
        if (await RefuseUnlessForQuoteAsync(id, Policies.CanEditQuote, EditRule, ct) is { } refused)
            return refused;
        return Respond(await _workflow.UpdateDetailsAsync(id,
            new QuoteDetailsInput(request.Site, request.Project, request.CustomerReference, request.DeliveryAddress), ct), id);
    }

    /// <summary>POST /api/quotes/{id}/submit (US-16)</summary>
    [HttpPost("{id:int}/submit")]
    [ProducesResponseType<QuoteDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Submit(int id, CancellationToken ct)
    {
        if (await RefuseUnlessForQuoteAsync(id, Policies.CanEditQuote, EditRule, ct) is { } refused)
            return refused;
        return Respond(await _workflow.SubmitAsync(id, ct), id);
    }

    /// <summary>POST /api/quotes/{id}/approve (US-17, US-18). Managing Director only.</summary>
    [HttpPost("{id:int}/approve")]
    [ProducesResponseType<QuoteDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Approve(int id, CancellationToken ct)
    {
        if (await RefuseUnlessAsync(Policies.CanApproveQuote, "Only the Managing Director approves quotes.") is { } refused)
            return refused;
        return Respond(await _workflow.ApproveAsync(id, ct), id);
    }

    /// <summary>POST /api/quotes/{id}/send: the approved quotation has gone to the customer.</summary>
    [HttpPost("{id:int}/send")]
    [ProducesResponseType<QuoteDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Send(int id, CancellationToken ct) =>
        Respond(await _workflow.MarkSentAsync(id, ct), id);

    /// <summary>POST /api/quotes/{id}/accept: the customer signed and returned it. Managing Director only.</summary>
    [HttpPost("{id:int}/accept")]
    [ProducesResponseType<QuoteDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Accept(int id, CancellationToken ct)
    {
        if (await RefuseUnlessAsync(Policies.CanApproveQuote, "Only the Managing Director records an accepted quote.") is { } refused)
            return refused;
        return Respond(await _workflow.MarkAcceptedAsync(id, ct), id);
    }

    /// <summary>POST /api/quotes/{id}/reopen (US-20): a new version after a counter-offer or once lapsed.</summary>
    [HttpPost("{id:int}/reopen")]
    [ProducesResponseType<QuoteDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reopen(int id, CancellationToken ct)
    {
        if (await RefuseUnlessForQuoteAsync(id, Policies.CanReopenQuote,
                "The Managing Director can reopen any quote. An estimator can reopen only their own.", ct) is { } refused)
            return refused;
        return Respond(await _workflow.ReopenAsync(id, ct), id);
    }

    private const string EditRule =
        "The Managing Director can change any quote. An estimator can change only their own quote while it is a draft.";

    private async Task<IActionResult?> RefuseUnlessAsync(string policy, string rule)
    {
        var allowed = await _authorization.AuthorizeAsync(User, policy);
        return allowed.Succeeded ? null : Forbidden(rule);
    }

    /// <summary>A resource-based check, which needs the quote itself.</summary>
    private async Task<IActionResult?> RefuseUnlessForQuoteAsync(int id, string policy, string rule, CancellationToken ct)
    {
        var quote = await _quotes.GetAsync(id, ct);
        if (quote is null)
            return NotFoundProblem(id);

        var allowed = await _authorization.AuthorizeAsync(User, quote, policy);
        return allowed.Succeeded ? null : Forbidden(rule);
    }

    private ObjectResult Forbidden(string rule) =>
        Problem(statusCode: StatusCodes.Status403Forbidden, title: "You cannot do this", detail: rule);

    private ObjectResult NotFoundProblem(int id) =>
        Problem(statusCode: StatusCodes.Status404NotFound, title: "Quote not found", detail: $"There is no quote {id}.");

    private IActionResult Respond(WorkflowResult result, int id)
    {
        switch (result.Outcome)
        {
            case WorkflowOutcome.Ok:
                return Ok(result.Quote);

            case WorkflowOutcome.NotFound:
                return NotFoundProblem(id);

            case WorkflowOutcome.ReferenceTaken:
                return Problem(statusCode: StatusCodes.Status409Conflict, title: "Reference already in use", detail: result.Problem);

            case WorkflowOutcome.NotAllowed:
                return Problem(statusCode: StatusCodes.Status409Conflict, title: "This step is not allowed now", detail: result.Problem);

            case WorkflowOutcome.Invalid:
                foreach (var (field, messages) in result.Errors!)
                    foreach (var message in messages)
                        ModelState.AddModelError(field, message);
                return ValidationProblem(ModelState);

            default:
                return Problem(statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}

/// <summary>The body of POST /api/quotes. Lengths match the database columns.</summary>
public sealed class CreateQuoteRequest
{
    /// <summary>Typed by hand; must be unique.</summary>
    [Required, StringLength(40)]
    public string Reference { get; set; } = "";

    [Required]
    public int? CustomerId { get; set; }

    [Required]
    public int? ContactId { get; set; }

    /// <summary>Entered on every quote; there is no default.</summary>
    [Required, Range(0, 999.99)]
    public decimal? MarkupPercent { get; set; }

    [StringLength(200)]
    public string? Site { get; set; }

    [StringLength(200)]
    public string? Project { get; set; }

    [StringLength(100)]
    public string? CustomerReference { get; set; }

    [StringLength(300)]
    public string? DeliveryAddress { get; set; }
}

/// <summary>The body of PUT /api/quotes/{id}/details.</summary>
public sealed class QuoteDetailsRequest
{
    [StringLength(200)]
    public string? Site { get; set; }

    [StringLength(200)]
    public string? Project { get; set; }

    [StringLength(100)]
    public string? CustomerReference { get; set; }

    [StringLength(300)]
    public string? DeliveryAddress { get; set; }
}
