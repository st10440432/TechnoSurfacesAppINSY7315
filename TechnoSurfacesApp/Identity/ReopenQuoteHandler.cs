using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfacesApp.Identity;

/// <summary>The CanReopenQuote policy's requirement. It needs the Quote to decide.</summary>
public sealed class ReopenQuoteRequirement : IAuthorizationRequirement { }

/// <summary>
/// Who may reopen a quote after a counter-offer or once it has lapsed (US-20): the
/// Managing Director any quote, an estimator only a quote they created (team
/// decision). Unlike CanEditQuote this does not require a draft, because only a
/// sent, accepted or expired quote can be reopened. Whether the quote's status
/// allows it is the lifecycle's decision, not this handler's.
///
/// Usage, with the quote loaded:
///   var allowed = await _authorization.AuthorizeAsync(User, quote, Policies.CanReopenQuote);
/// </summary>
public sealed class ReopenQuoteHandler : AuthorizationHandler<ReopenQuoteRequirement, Quote>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ReopenQuoteRequirement requirement, Quote quote)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (context.User.IsInRole(Roles.ManagingDirector)
            || (!string.IsNullOrEmpty(userId) && quote.CreatedByUserId == userId))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
