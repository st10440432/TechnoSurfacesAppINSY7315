using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfacesApp.Identity;

namespace TechnoSurfacesApp.Platform;

/// GET /health. Healthy only when the app is running, can reach the database
public static class HealthChecks
{
    public const string Path = "/health";

    public static IServiceCollection AddPlatformHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddDbContextCheck<TechnoSurfacesDbContext>("database")
            .AddCheck<MigrationsAppliedCheck<TechnoSurfacesDbContext>>("domain-migrations")
            .AddCheck<MigrationsAppliedCheck<AuthDbContext>>("auth-migrations");
        return services;
    }

    public static void MapPlatformHealthChecks(this WebApplication app) =>
        app.MapHealthChecks(Path).AllowAnonymous().DisableRateLimiting();
}

public sealed class MigrationsAppliedCheck<TContext> : IHealthCheck where TContext : DbContext
{
    private readonly TContext _db;

    public MigrationsAppliedCheck(TContext db) => _db = db;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var pending = (await _db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        return pending.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy($"{pending.Count} migration(s) not applied to {typeof(TContext).Name}.");
    }
}
