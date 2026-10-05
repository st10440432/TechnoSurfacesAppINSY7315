using System.Threading.RateLimiting;

namespace TechnoSurfacesApp.Platform;

public static class SignInRateLimiting
{
    public const string SignInPath = "/Account/Login";
    public const int PermitLimit = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddSignInRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var isSignIn = HttpMethods.IsPost(context.Request.Method)
                    && context.Request.Path.Equals(SignInPath, StringComparison.OrdinalIgnoreCase);

                if (!isSignIn)
                    return RateLimitPartition.GetNoLimiter("not-sign-in");

                var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(client, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PermitLimit,
                    Window = Window,
                    QueueLimit = 0
                });
            });
        });
}