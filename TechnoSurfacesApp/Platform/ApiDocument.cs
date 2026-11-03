using Microsoft.OpenApi;

namespace TechnoSurfacesApp.Platform;

/// <summary>
/// GET /api/openapi/v1.json: the OpenAPI document for every /api endpoint, with its
/// route, request and response bodies and error responses, so another client can
/// discover the API. Built into .NET 10 (Microsoft.AspNetCore.OpenApi). It sits under
/// /api and needs sign-in like everything else, so a signed-out request is answered
/// 401 as every other /api call is. There is no viewer page: the security policy
/// allows no script from a CDN, and the JSON document is what a client reads.
/// </summary>
public static class ApiDocument
{
    public const string Path = "/api/openapi/{documentName}.json";

    public static IServiceCollection AddApiDocument(this IServiceCollection services) =>
        services.AddOpenApi("v1", options =>
        {
            // The /api controllers only. The screens are routed by convention and are
            // never part of the API.
            options.ShouldInclude = description => description.RelativePath?.StartsWith("api/", StringComparison.Ordinal) == true;

            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Techno Surfaces quoting API",
                    Version = "v1",
                    Description =
                        "The API the quoting screens use: the material catalogue, the costing sheet, the customer quotation, " +
                        "the quote workflow, customers and the Sage Pastel invoice record. Every call needs a signed-in session " +
                        "(the TechnoSurfaces.Auth cookie from the sign-in page). Every POST, PUT and DELETE also needs the " +
                        "antiforgery token from a signed-in page in the RequestVerificationToken header. Errors are " +
                        "application/problem+json. A material price that cannot be resolved is 422 and nothing is saved."
                };
                return Task.CompletedTask;
            });

            // Not signed in is the same answer on every endpoint, so it is added once here
            // rather than on every action.
            options.AddOperationTransformer((operation, _, _) =>
            {
                operation.Responses ??= new OpenApiResponses();
                operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Not signed in." });
                return Task.CompletedTask;
            });
        });

    public static void MapApiDocument(this WebApplication app) => app.MapOpenApi(Path);
}
