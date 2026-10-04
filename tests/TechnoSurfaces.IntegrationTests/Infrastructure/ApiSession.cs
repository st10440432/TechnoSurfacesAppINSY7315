using System.Collections.Concurrent;
using System.Net.Http.Json;

namespace TechnoSurfaces.IntegrationTests.Infrastructure;

/// <summary>
/// A signed-in client for calling /api, with the antiforgery token a script sends
/// in the RequestVerificationToken header.
///
/// Each account signs in once per test run and the session is reused. Sign-in is
/// rate limited to ten attempts a minute from one address, and every test client
/// shares one address, so signing in per test would exhaust the allowance.
/// </summary>
public sealed class ApiSession
{
    private static readonly ConcurrentDictionary<(AppFactory, string), Lazy<Task<ApiSession>>> Sessions = new();

    private ApiSession(HttpClient client, string token)
    {
        Client = client;
        Token = token;
    }

    public HttpClient Client { get; }

    public string Token { get; }

    public static Task<ApiSession> ForAsync(AppFactory app, string email) =>
        Sessions.GetOrAdd((app, email), key => new Lazy<Task<ApiSession>>(() => SignInAsync(key.Item1, key.Item2))).Value;

    private static async Task<ApiSession> SignInAsync(AppFactory app, string email)
    {
        var client = app.CreateBrowser();
        await SignIn.AsAsync(client, email);
        var token = await SignIn.AntiforgeryTokenAsync(client, "/Home/Dashboard");
        return new ApiSession(client, token);
    }

    public Task<HttpResponseMessage> GetAsync(string path) => Client.GetAsync(path);

    public Task<HttpResponseMessage> PostAsync<T>(string path, T body, bool withToken = true) =>
        SendAsync(HttpMethod.Post, path, JsonContent.Create(body), withToken);

    public Task<HttpResponseMessage> PostAsync(string path, bool withToken = true) =>
        SendAsync(HttpMethod.Post, path, null, withToken);

    public Task<HttpResponseMessage> PutAsync<T>(string path, T body, bool withToken = true) =>
        SendAsync(HttpMethod.Put, path, JsonContent.Create(body), withToken);

    public Task<HttpResponseMessage> DeleteAsync(string path, bool withToken = true) =>
        SendAsync(HttpMethod.Delete, path, null, withToken);

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, bool withToken)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (withToken)
            request.Headers.Add("RequestVerificationToken", Token);
        return Client.SendAsync(request);
    }
}
