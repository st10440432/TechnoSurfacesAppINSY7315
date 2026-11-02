using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TechnoSurfaces.IntegrationTests.Infrastructure;

namespace TechnoSurfaces.IntegrationTests.Api;

/// <summary>
/// The OpenAPI document at /api/openapi/v1.json: it needs sign-in like every other
/// /api call, describes the /api endpoints and only those, and documents the 422 a
/// price that cannot be resolved is answered with.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ApiDocumentTests
{
    private const string DocumentPath = "/api/openapi/v1.json";

    private readonly AppFactory _app;

    public ApiDocumentTests(AppFactory app) => _app = app;

    private async Task<JsonElement> DocumentAsync()
    {
        var session = await ApiSession.ForAsync(_app, AppFactory.EstimatorEmail);
        var response = await session.GetAsync(DocumentPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static IEnumerable<string> Paths(JsonElement document) =>
        document.GetProperty("paths").EnumerateObject().Select(p => p.Name);

    [Fact]
    public async Task The_document_is_refused_when_signed_out()
    {
        var response = await _app.CreateBrowser().GetAsync(DocumentPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signed_in_it_lists_the_costing_endpoints()
    {
        var paths = Paths(await DocumentAsync()).ToList();

        Assert.Contains("/api/quotes/{quoteId}/costing", paths);
        Assert.Contains("/api/quotes/{quoteId}/lines", paths);
        Assert.Contains("/api/quotes/{quoteId}/lines/{lineId}", paths);
        Assert.Contains("/api/quotes/{quoteId}/totals", paths);
    }

    [Fact]
    public async Task It_describes_the_api_and_none_of_the_screens()
    {
        var paths = Paths(await DocumentAsync()).ToList();

        Assert.NotEmpty(paths);
        Assert.All(paths, p => Assert.StartsWith("/api/", p));
    }

    [Fact]
    public async Task Adding_a_line_documents_its_body_and_the_unresolved_price_422()
    {
        var addLine = (await DocumentAsync()).GetProperty("paths").GetProperty("/api/quotes/{quoteId}/lines").GetProperty("post");
        var responses = addLine.GetProperty("responses");

        Assert.True(addLine.TryGetProperty("requestBody", out _));
        Assert.True(responses.TryGetProperty("201", out _));
        Assert.True(responses.TryGetProperty("422", out _));
        Assert.True(responses.TryGetProperty("401", out _));
    }
}
