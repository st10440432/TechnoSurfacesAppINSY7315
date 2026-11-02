namespace TechnoSurfacesApp.Platform;
public static class SecurityHeaders
{
    // Task 1 8.7: scripts are restricted to the application's own origin. No view
    // has an inline script or an inline event handler (behaviour is attached from
    // wwwroot/js through data attributes), so script-src has no 'unsafe-inline' and
    // an injected script would not run. Styles come from the application's own
    // origin too: the dashboard bars are set from app.js rather than a style
    // attribute (ZAP finding, October 2026).
    public const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["Content-Security-Policy"] = ContentSecurityPolicy;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                headers["X-Frame-Options"] = "DENY";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                return Task.CompletedTask;
            });
            await next();
        });
}