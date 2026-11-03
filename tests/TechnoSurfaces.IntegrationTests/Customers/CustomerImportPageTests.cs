using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.IntegrationTests.Infrastructure;

namespace TechnoSurfaces.IntegrationTests.Customers;

/// <summary>
/// The import from Sage Pastel through the real screens and SQL Server: only the
/// Managing Director can open it, the preview saves nothing, the import saves what
/// the preview showed, and running it again changes nothing. The customers are made
/// up, in the layout of the client's export.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed partial class CustomerImportPageTests
{
    private readonly AppFactory _app;

    public CustomerImportPageTests(AppFactory app) => _app = app;

    private static string Listing(string code, string name, string contact) =>
        "\"Prepared by:  Example Trading (Pty) Ltd\"\r\n\"Customer Listing\"\r\n \r\n \r\n" +
        $"\"Acc\",\"{code}\",\"Telephone:\",\"021 555 0400\",\"Disc%:\",\"0.00%\"\r\n" +
        $"\"Name\",\"{name}\",\"Fax:\",\"\",\"Cr Limit:\",\"0\"\r\n" +
        $"\"Delivery Address\",\"Postal Address\",\"Contact:\",\"{contact}\",\"Interest:\",\"None\"\r\n" +
        "\"\",\"PO Box 4\",\"Mobile Phone:\",\"\",\"Statement:\",\"P OM\"\r\n" +
        "\"\",\"Milnerton 7435\",\"Email:\",\"orders@example.co.za\",\"Blocked:\",\"No\"\r\n" +
        "\"\",\"\",\"Type:\",\"O/I\",\"Price List:\",\"Price List 1\"\r\n" +
        "\"\",\"\",,\"\",\"Tax Reference:\",\"4222222222\"\r\n \r\n \r\n";

    private static async Task<HttpResponseMessage> UploadAsync(ApiSession session, string listing)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(session.Token), "__RequestVerificationToken" }
        };
        var file = new ByteArrayContent(Encoding.ASCII.GetBytes(listing));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "listing", "Customer Listing.txt");
        return await session.Client.PostAsync("/Customers/Import", form);
    }

    private static Task<HttpResponseMessage> ConfirmAsync(ApiSession session, string previewKey) =>
        session.Client.PostAsync("/Customers/ConfirmImport", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["previewKey"] = previewKey,
            ["__RequestVerificationToken"] = session.Token
        }));

    private async Task<int> CustomersWithCodeAsync(string code)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();
        return await db.Customers.CountAsync(c => c.AccountCode == code);
    }

    private static string NewCode() => "T" + Guid.NewGuid().ToString("N")[..9].ToUpperInvariant();

    [Fact]
    public async Task An_estimator_cannot_open_the_import()
    {
        var estimator = await ApiSession.ForAsync(_app, AppFactory.EstimatorEmail);

        var response = await estimator.GetAsync("/Customers/Import");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/AccessDenied", response.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task An_estimator_cannot_import_even_by_posting_the_form()
    {
        var estimator = await ApiSession.ForAsync(_app, AppFactory.EstimatorEmail);
        var code = NewCode();

        var response = await UploadAsync(estimator, Listing(code, "Estimator Upload", "Zanele"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/AccessDenied", response.Headers.Location!.PathAndQuery);
        Assert.Equal(0, await CustomersWithCodeAsync(code));
    }

    [Fact]
    public async Task The_managing_director_previews_then_imports_and_a_second_run_adds_nothing()
    {
        var md = await ApiSession.ForAsync(_app, AppFactory.ManagingDirectorEmail);
        var code = NewCode();
        var listing = Listing(code, "Preview Then Import", "Zanele");

        var preview = await UploadAsync(md, listing);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var html = await preview.Content.ReadAsStringAsync();
        Assert.Contains("Preview Then Import", html);
        Assert.Contains("Milnerton", html);
        Assert.Equal(0, await CustomersWithCodeAsync(code));

        var key = PreviewKeyField().Match(html);
        Assert.True(key.Success, "The preview has no import button.");
        var imported = await ConfirmAsync(md, key.Groups[1].Value);
        Assert.Equal(HttpStatusCode.Redirect, imported.StatusCode);
        Assert.Equal(1, await CustomersWithCodeAsync(code));

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();
            var customer = await db.Customers.Include(c => c.Contacts).SingleAsync(c => c.AccountCode == code);
            Assert.Equal(("PO Box 4", "Milnerton", "7435", "4222222222"), (customer.AddressLine1, customer.City, customer.PostalCode, customer.VatNumber));
            Assert.Equal("Zanele", Assert.Single(customer.Contacts).FullName);
        }

        var again = await (await UploadAsync(md, listing)).Content.ReadAsStringAsync();
        Assert.Contains("Already in the app with this account code", again);
        Assert.DoesNotMatch(PreviewKeyField(), again);
    }

    [Fact]
    public async Task A_preview_cannot_be_imported_twice()
    {
        var md = await ApiSession.ForAsync(_app, AppFactory.ManagingDirectorEmail);
        var code = NewCode();
        var html = await (await UploadAsync(md, Listing(code, "Only Once", "Zanele"))).Content.ReadAsStringAsync();
        var key = PreviewKeyField().Match(html).Groups[1].Value;

        await ConfirmAsync(md, key);
        var second = await ConfirmAsync(md, key);

        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.EndsWith("/Customers/Import", second.Headers.Location!.OriginalString);
        Assert.Equal(1, await CustomersWithCodeAsync(code));
    }

    [GeneratedRegex("name=\"previewKey\" value=\"([0-9a-f]{32})\"")]
    private static partial Regex PreviewKeyField();
}
