using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.IntegrationTests.Infrastructure;

namespace TechnoSurfaces.IntegrationTests.Quoting;

/// <summary>
/// A quote taken end to end over HTTP against SQL Server, by the people allowed to
/// take each step: an estimator creates, prices and submits it; the Managing
/// Director approves it; it is sent; the Managing Director records it accepted; the
/// estimator reopens it as version 2. Each refusal on the way is checked too.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class QuoteWorkflowApiTests
{
    private const string OtherEstimatorEmail = "devan@technosurfaces.co.za";

    private readonly AppFactory _app;

    public QuoteWorkflowApiTests(AppFactory app) => _app = app;

    private Task<ApiSession> AsAsync(string email) => ApiSession.ForAsync(_app, email);

    private async Task<(int CustomerId, int ContactId, int SandingId)> SeededIdsAsync()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();
        var customer = await db.Customers.Include(c => c.Contacts).SingleAsync(c => c.AccountCode == "RAW001");
        var sanding = await db.RateItems.Where(r => r.Name == "Sanding time").Select(r => r.Id).SingleAsync();
        return (customer.Id, customer.Contacts.First().Id, sanding);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static string Status(JsonElement quote) => quote.GetProperty("status").GetString()!;

    /// <summary>
    /// A quote priced at 3 hours of sanding (R300, R441 with 47% markup), with the
    /// customer quotation line written to match.
    /// </summary>
    private async Task<int> CreatePricedQuoteAsync(ApiSession author)
    {
        var (customerId, contactId, sanding) = await SeededIdsAsync();
        var created = await author.PostAsync("/api/quotes", new
        {
            reference = "IT-WF-" + Guid.NewGuid().ToString("N")[..8],
            customerId,
            contactId,
            markupPercent = 47m,
            site = "Tokai",
            project = "Kitchen"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonAsync(created)).GetProperty("id").GetInt32();

        var line = await author.PostAsync($"/api/quotes/{id}/lines", new { type = "rate", rateItemId = sanding, quantity = 3m });
        Assert.Equal(HttpStatusCode.Created, line.StatusCode);

        var quotationLine = await author.PostAsync($"/api/quotes/{id}/quotation-lines",
            new { room = "Kitchen", description = "Countertop, fabricate and install", amountExVat = 441m });
        Assert.Equal(HttpStatusCode.Created, quotationLine.StatusCode);
        return id;
    }

    [Fact]
    public async Task A_quote_is_taken_end_to_end_by_the_people_allowed_each_step()
    {
        var estimator = await AsAsync(AppFactory.EstimatorEmail);
        var md = await AsAsync(AppFactory.ManagingDirectorEmail);
        var otherEstimator = await AsAsync(OtherEstimatorEmail);

        var id = await CreatePricedQuoteAsync(estimator);

        // Submitted by its author; another estimator may not submit it.
        Assert.Equal(HttpStatusCode.Forbidden, (await otherEstimator.PostAsync($"/api/quotes/{id}/submit")).StatusCode);
        var submitted = await estimator.PostAsync($"/api/quotes/{id}/submit");
        Assert.Equal("PendingApproval", Status(await JsonAsync(submitted)));

        // In the Managing Director's queue; an estimator cannot see the queue or approve.
        Assert.Equal(HttpStatusCode.Forbidden, (await estimator.GetAsync("/api/quotes/approval-queue")).StatusCode);
        var queue = await JsonAsync(await md.GetAsync("/api/quotes/approval-queue"));
        Assert.Contains(queue.EnumerateArray(), q => q.GetProperty("id").GetInt32() == id);
        Assert.Equal(HttpStatusCode.Forbidden, (await estimator.PostAsync($"/api/quotes/{id}/approve")).StatusCode);

        // The Managing Director corrects the pending quote. The new markup moves the
        // costing total, so approval is refused until the quotation matches (US-10).
        var corrected = await md.PutAsync($"/api/quotes/{id}/costing", new { markupPercent = 45m });
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await md.PostAsync($"/api/quotes/{id}/approve")).StatusCode);

        var check = await JsonAsync(await md.GetAsync($"/api/quotes/{id}/quotation/check"));
        Assert.Equal(435m, check.GetProperty("costingTotalExVat").GetDecimal());
        var lineId = (await JsonAsync(await md.GetAsync($"/api/quotes/{id}/quotation"))).GetProperty("lines")[0].GetProperty("id").GetInt32();
        var fixedLine = await md.PutAsync($"/api/quotes/{id}/quotation-lines/{lineId}",
            new { room = "Kitchen", description = "Countertop, fabricate and install", amountExVat = 435m });
        Assert.Equal(HttpStatusCode.OK, fixedLine.StatusCode);

        var approved = await md.PostAsync($"/api/quotes/{id}/approve");
        Assert.Equal("Approved", Status(await JsonAsync(approved)));

        // The issued document: the customer's figures, none of the costing.
        var quotation = await md.GetAsync($"/api/quotes/{id}/quotation");
        var document = await quotation.Content.ReadAsStringAsync();
        Assert.Contains("\"isIssued\":true", document);
        Assert.Contains("\"totalIncVat\":500.25", document);
        Assert.DoesNotContain("Sanding time", document);
        Assert.DoesNotContain("markup", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Rate card", document);

        // Sent by the estimator; accepted by the Managing Director only.
        Assert.Equal("Sent", Status(await JsonAsync(await estimator.PostAsync($"/api/quotes/{id}/send"))));
        Assert.Equal(HttpStatusCode.Forbidden, (await estimator.PostAsync($"/api/quotes/{id}/accept")).StatusCode);
        Assert.Equal("Accepted", Status(await JsonAsync(await md.PostAsync($"/api/quotes/{id}/accept"))));

        // Reopened by its author after a counter-offer; another estimator may not.
        Assert.Equal(HttpStatusCode.Forbidden, (await otherEstimator.PostAsync($"/api/quotes/{id}/reopen")).StatusCode);
        var reopened = await JsonAsync(await estimator.PostAsync($"/api/quotes/{id}/reopen"));
        Assert.Equal("Draft", Status(reopened));
        Assert.Equal(2, reopened.GetProperty("versionNo").GetInt32());

        var versions = await JsonAsync(await estimator.GetAsync($"/api/quotes/{id}/versions"));
        Assert.Equal(2, versions.GetArrayLength());
        Assert.True(versions[0].GetProperty("isSealed").GetBoolean());
    }

    [Fact]
    public async Task Another_estimator_cannot_write_the_quotation_lines()
    {
        var estimator = await AsAsync(AppFactory.EstimatorEmail);
        var otherEstimator = await AsAsync(OtherEstimatorEmail);
        var id = await CreatePricedQuoteAsync(estimator);

        var response = await otherEstimator.PostAsync($"/api/quotes/{id}/quotation-lines",
            new { description = "Extra", amountExVat = 10m });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await otherEstimator.GetAsync($"/api/quotes/{id}/quotation")).StatusCode);
    }

    [Fact]
    public async Task Approving_an_estimators_draft_directly_is_409()
    {
        var estimator = await AsAsync(AppFactory.EstimatorEmail);
        var md = await AsAsync(AppFactory.ManagingDirectorEmail);
        var id = await CreatePricedQuoteAsync(estimator);

        var response = await md.PostAsync($"/api/quotes/{id}/approve");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_managing_directors_own_quote_is_approved_directly()
    {
        var md = await AsAsync(AppFactory.ManagingDirectorEmail);
        var id = await CreatePricedQuoteAsync(md);

        var approved = await md.PostAsync($"/api/quotes/{id}/approve");

        Assert.Equal("Approved", Status(await JsonAsync(approved)));
    }

    [Fact]
    public async Task A_quote_is_created_with_its_own_validity_period_or_thirty_days()
    {
        var estimator = await AsAsync(AppFactory.EstimatorEmail);
        var (customerId, contactId, _) = await SeededIdsAsync();

        var custom = await JsonAsync(await estimator.PostAsync("/api/quotes", new
        {
            reference = "IT-VAL-" + Guid.NewGuid().ToString("N")[..6], customerId, contactId, markupPercent = 40m, validForDays = 45
        }));
        var standard = await JsonAsync(await estimator.PostAsync("/api/quotes", new
        {
            reference = "IT-VAL-" + Guid.NewGuid().ToString("N")[..6], customerId, contactId, markupPercent = 40m
        }));
        var invalid = await estimator.PostAsync("/api/quotes", new
        {
            reference = "IT-VAL-" + Guid.NewGuid().ToString("N")[..6], customerId, contactId, markupPercent = 40m, validForDays = 0
        });

        Assert.Equal(45, custom.GetProperty("daysRemaining").GetInt32());
        Assert.Equal(30, standard.GetProperty("daysRemaining").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task A_duplicate_reference_is_409_and_missing_fields_are_400()
    {
        var estimator = await AsAsync(AppFactory.EstimatorEmail);
        var (customerId, contactId, _) = await SeededIdsAsync();
        var reference = "IT-DUP-" + Guid.NewGuid().ToString("N")[..6];
        await estimator.PostAsync("/api/quotes", new { reference, customerId, contactId, markupPercent = 40m });

        var duplicate = await estimator.PostAsync("/api/quotes", new { reference, customerId, contactId, markupPercent = 40m });
        var missing = await estimator.PostAsync("/api/quotes", new { reference = "IT-NOMARKUP", customerId, contactId });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.True((await JsonAsync(missing)).GetProperty("errors").TryGetProperty("MarkupPercent", out _));
    }

    [Fact]
    public async Task Two_versions_of_a_quote_cannot_share_a_version_number()
    {
        // The unique index on (QuoteId, VersionNo) is what makes a version number
        // identify one snapshot. Written straight to the database to prove the
        // database itself refuses it, not only the domain.
        var estimator = await AsAsync(AppFactory.EstimatorEmail);
        var id = await CreatePricedQuoteAsync(estimator);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();
        var duplicate = db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO QuoteVersions (QuoteId, VersionNo, CreatedByUserId, CreatedAtUtc, MarkupPercent, VatRate, TransportAmount, IsSealed)
               VALUES ({id}, 1, 'test', SYSUTCDATETIME(), 40, 0.15, 0, 0)");

        await Assert.ThrowsAsync<SqlException>(() => duplicate);
        Assert.Equal(1, await db.QuoteVersions.CountAsync(v => v.QuoteId == id));
    }

    [Fact]
    public async Task Every_version_stays_readable_as_it_was_issued_after_a_revision()
    {
        var md = await AsAsync(AppFactory.ManagingDirectorEmail);
        var estimator = await AsAsync(AppFactory.EstimatorEmail);
        var id = await CreatePricedQuoteAsync(md);

        // Version 1 is issued and sent, then reopened after a counter-offer.
        Assert.Equal("Approved", Status(await JsonAsync(await md.PostAsync($"/api/quotes/{id}/approve"))));
        Assert.Equal("Sent", Status(await JsonAsync(await md.PostAsync($"/api/quotes/{id}/send"))));
        Assert.Equal(2, (await JsonAsync(await md.PostAsync($"/api/quotes/{id}/reopen"))).GetProperty("versionNo").GetInt32());

        // Version 2 changes: a new markup, a new site and another quotation line.
        Assert.Equal(HttpStatusCode.OK, (await md.PutAsync($"/api/quotes/{id}/costing", new { markupPercent = 60m })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await md.PutAsync($"/api/quotes/{id}/details", new { site = "Constantia", project = "Kitchen" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await md.PostAsync($"/api/quotes/{id}/quotation-lines",
            new { room = "Scullery", description = "Scullery top", amountExVat = 39m })).StatusCode);

        // Version 1 reads exactly as issued, to anyone signed in.
        var first = await JsonAsync(await estimator.GetAsync($"/api/quotes/{id}/versions/1/quotation"));
        Assert.Equal(1, first.GetProperty("versionNo").GetInt32());
        Assert.False(first.GetProperty("isCurrentVersion").GetBoolean());
        Assert.True(first.GetProperty("headingAsIssued").GetBoolean());
        Assert.Equal("Tokai", first.GetProperty("header").GetProperty("site").GetString());
        Assert.Equal(1, first.GetProperty("lines").GetArrayLength());
        Assert.Equal(441m, first.GetProperty("totals").GetProperty("subtotalExVat").GetDecimal());

        var firstCosting = await JsonAsync(await estimator.GetAsync($"/api/quotes/{id}/versions/1/costing"));
        Assert.True(firstCosting.GetProperty("isSealed").GetBoolean());
        Assert.Equal(47m, firstCosting.GetProperty("totals").GetProperty("markupPercent").GetDecimal());
        Assert.Equal(441m, firstCosting.GetProperty("totals").GetProperty("totalExVat").GetDecimal());

        // Version 2 is the quote as it stands.
        var second = await JsonAsync(await md.GetAsync($"/api/quotes/{id}/versions/2/quotation"));
        Assert.True(second.GetProperty("isCurrentVersion").GetBoolean());
        Assert.Equal("Constantia", second.GetProperty("header").GetProperty("site").GetString());
        Assert.Equal(2, second.GetProperty("lines").GetArrayLength());
        var secondCosting = await JsonAsync(await md.GetAsync($"/api/quotes/{id}/versions/2/costing"));
        Assert.Equal(60m, secondCosting.GetProperty("totals").GetProperty("markupPercent").GetDecimal());

        // The history says who approved version 1, and a version that does not exist is 404.
        var versions = await JsonAsync(await estimator.GetAsync($"/api/quotes/{id}/versions"));
        Assert.True(versions[0].GetProperty("isIssued").GetBoolean());
        Assert.False(versions[1].GetProperty("isIssued").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await md.GetAsync($"/api/quotes/{id}/versions/9/quotation")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await md.GetAsync($"/api/quotes/{id}/versions/9/costing")).StatusCode);

        // The screen opens for both versions.
        var page = await estimator.GetAsync($"/Quotes/Version/{id}?number=1");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("This is the version the customer was sent", html);
        Assert.Contains("Countertop, fabricate and install", html);
        Assert.DoesNotContain("Scullery top", html);
        Assert.Equal(HttpStatusCode.OK, (await md.GetAsync($"/Quotes/Version/{id}?number=2")).StatusCode);
    }
}
