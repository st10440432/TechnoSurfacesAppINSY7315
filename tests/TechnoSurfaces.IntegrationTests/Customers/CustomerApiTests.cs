using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TechnoSurfaces.IntegrationTests.Infrastructure;

namespace TechnoSurfaces.IntegrationTests.Customers;

/// <summary>
/// Customers and contacts over HTTP against SQL Server (US-15). The app starts as it
/// does in Azure, so the first test also proves RA Woodcraft is seeded on start-up.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class CustomerApiTests
{
    private readonly AppFactory _app;

    public CustomerApiTests(AppFactory app) => _app = app;

    private Task<ApiSession> EstimatorAsync() => ApiSession.ForAsync(_app, AppFactory.EstimatorEmail);

    private static string NewAccountCode() => "IT" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    public async Task Ra_woodcraft_is_seeded_on_start_up_and_offered_for_a_new_quote()
    {
        var client = await EstimatorAsync();

        var choices = await (await client.GetAsync("/api/customers/choices")).Content.ReadFromJsonAsync<JsonElement>();
        var raWoodcraft = choices.EnumerateArray().Single(c => c.GetProperty("accountCode").GetString() == "RAW001");

        Assert.Equal("RA Woodcraft", raWoodcraft.GetProperty("name").GetString());
        Assert.Equal("Accounts", raWoodcraft.GetProperty("contacts")[0].GetProperty("fullName").GetString());
    }

    [Fact]
    public async Task An_estimator_creates_a_customer_and_adds_a_contact()
    {
        var client = await EstimatorAsync();

        var created = await client.PostAsync("/api/customers", new { name = "Integration Kitchens", accountCode = NewAccountCode() });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var contact = await client.PostAsync($"/api/customers/{id}/contacts", new { fullName = "Site manager", email = "site@example.com" });
        Assert.Equal(HttpStatusCode.Created, contact.StatusCode);

        var read = await (await client.GetAsync($"/api/customers/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, read.GetProperty("contacts").GetArrayLength());
    }

    [Fact]
    public async Task A_pastel_account_code_already_in_use_is_409()
    {
        var client = await EstimatorAsync();
        var code = NewAccountCode();
        await client.PostAsync("/api/customers", new { name = "First", accountCode = code });

        var duplicate = await client.PostAsync("/api/customers", new { name = "Second", accountCode = code });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("application/problem+json", duplicate.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_customer_without_a_name_is_400()
    {
        var client = await EstimatorAsync();

        var response = await client.PostAsync("/api/customers", new { name = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_deactivated_customer_leaves_the_new_quote_choice()
    {
        var client = await EstimatorAsync();
        var created = await client.PostAsync("/api/customers", new { name = "Leaving customer", accountCode = NewAccountCode() });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        await client.PostAsync($"/api/customers/{id}/contacts", new { fullName = "Contact" });

        var deactivated = await client.PostAsync($"/api/customers/{id}/deactivate");
        var choices = await (await client.GetAsync("/api/customers/choices")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.DoesNotContain(choices.EnumerateArray(), c => c.GetProperty("id").GetInt32() == id);
    }
}
