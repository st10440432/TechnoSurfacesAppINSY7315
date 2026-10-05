using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Quoting;
using TechnoSurfacesApp.Identity;

namespace TechnoSurfaces.UnitTests.Security;

/// <summary>
/// Task 1 2.2: the MD edits any quote; an estimator edits only their own draft.
/// Covers three of the build plan's security tests.
/// </summary>
public sealed class EditQuoteHandlerTests
{
    private const string Lerato = "lerato-id";
    private const string Devan = "devan-id";
    private const string Paul = "paul-id";

    private static ClaimsPrincipal Person(string? id, string role)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, role) };
        if (id is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, id));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static Quote QuoteBy(string authorId, QuoteStatus status = QuoteStatus.Draft)
    {
        var quote = new Quote("TS-2026-0001", 1, 1, authorId, new DateOnly(2026, 10, 2));
        quote.StartNewVersion(authorId, 30m);

        // Status has no setter: the quote is moved through its real lifecycle
        // (Task 1 5.2.1), so the test cannot build a state the app never reaches.
        if (status == QuoteStatus.Draft) return quote;

        quote.Submit();
        if (status == QuoteStatus.PendingApproval) return quote;

        quote.Approve(Paul);
        if (status == QuoteStatus.Approved) return quote;

        quote.MarkSent();
        if (status == QuoteStatus.Sent) return quote;

        throw new ArgumentOutOfRangeException(nameof(status), status, "Not used by these tests.");
    }

    private static async Task<bool> CanEdit(ClaimsPrincipal user, Quote quote)
    {
        var requirement = new EditQuoteRequirement();
        var context = new AuthorizationHandlerContext(new[] { requirement }, user, quote);
        await new EditQuoteHandler().HandleAsync(context);
        return context.HasSucceeded;
    }

    [Fact]
    public async Task An_estimator_may_edit_their_own_draft() =>
        Assert.True(await CanEdit(Person(Lerato, Roles.Estimator), QuoteBy(Lerato)));

    [Fact]
    public async Task An_estimator_may_not_edit_another_estimators_draft() =>
        Assert.False(await CanEdit(Person(Devan, Roles.Estimator), QuoteBy(Lerato)));

    [Theory]
    [InlineData(QuoteStatus.PendingApproval)]
    [InlineData(QuoteStatus.Approved)]
    [InlineData(QuoteStatus.Sent)]
    public async Task An_estimators_own_quote_is_locked_once_submitted(QuoteStatus status) =>
        Assert.False(await CanEdit(Person(Lerato, Roles.Estimator), QuoteBy(Lerato, status)));

    [Theory]
    [InlineData(QuoteStatus.Draft)]
    [InlineData(QuoteStatus.PendingApproval)]
    [InlineData(QuoteStatus.Approved)]
    public async Task The_managing_director_may_edit_any_quote(QuoteStatus status) =>
        Assert.True(await CanEdit(Person(Paul, Roles.ManagingDirector), QuoteBy(Lerato, status)));

    [Fact]
    public async Task A_user_without_an_id_is_refused() =>
        Assert.False(await CanEdit(Person(null, Roles.Estimator), QuoteBy(Lerato)));
}