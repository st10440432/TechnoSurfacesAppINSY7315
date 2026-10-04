using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfacesApp.Identity;

/// <summary>The CanEditQuote policy's requirement. It needs the Quote to decide.</summary>
public sealed class EditQuoteRequirement : IAuthorizationRequirement { }

/// <summary>
/// Resource-based authorisation (Task 1 2.2): the Managing Director may edit any
/// quote; an estimator may edit only a quote they created that is still a draft.
/// Once submitted it is locked to them. Anything else is refused.
///
/// Usage, with the quote loaded:
///   var allowed = await _authorization.AuthorizeAsync(User, quote, Policies.CanEditQuote);
///   if (!allowed.Succeeded) return Forbid();
/// </summary>
public sealed class EditQuoteHandler : AuthorizationHandler<EditQuoteRequirement, Quote>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, EditQuoteRequirement requirement, Quote quote)
    {
        if (context.User.IsInRole(Roles.ManagingDirector))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!string.IsNullOrEmpty(userId)
            && quote.CreatedByUserId == userId
            && quote.Status == QuoteStatus.Draft)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}