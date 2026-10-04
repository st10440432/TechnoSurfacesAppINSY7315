using System.Net;
using System.Text.RegularExpressions;

namespace TechnoSurfaces.IntegrationTests.Infrastructure;

/// <summary>
/// Signs a test client in through the real sign-in form, including its antiforgery
/// token, exactly as a browser would.
/// </summary>
public static partial class SignIn
{
    public static async Task<string> AntiforgeryTokenAsync(HttpClient client, string path = "/Account/Login")
    {
        var html = await client.GetStringAsync(path);
        var match = TokenField().Match(html);
        if (!match.Success)
            throw new InvalidOperationException($"No antiforgery token found on {path}.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, string email, string password)
    {
        var token = await AntiforgeryTokenAsync(client);
        return await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["__RequestVerificationToken"] = token
        }));
    }

    public static async Task AsAsync(HttpClient client, string email)
    {
        var response = await PostAsync(client, email, AppFactory.DemoPassword);
        if (response.StatusCode != HttpStatusCode.Redirect)
            throw new InvalidOperationException($"Sign-in as {email} failed with {(int)response.StatusCode}.");
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenField();
}