using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Infrastructure;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfacesApp.Identity;
using TechnoSurfacesApp.Services;
using TechnoSurfaces.Application.Auditing;
using TechnoSurfaces.Infrastructure.Data.Auditing;
using TechnoSurfaces.Application.Catalogue;
using TechnoSurfacesApp.Platform;


var builder = WebApplication.CreateBuilder(args);

// One database, two contexts: the domain model (Kallan) and the credential store.
// Fails at startup rather than running without a database - the same fail-closed
// rule the pricing follows.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

builder.Services.AddTechnoSurfaces(connectionString);

// NFR-04: the audit interceptor is attached to the domain context here, so
// Kallan's registration stays unchanged and no save can skip the audit trail.
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddScoped<AuditInterceptor>();
builder.Services.ConfigureDbContext<TechnoSurfacesDbContext>((services, options) =>
    options.AddInterceptors(services.GetRequiredService<AuditInterceptor>()));

builder.Services.AddScoped<ICatalogueService, CatalogueService>();
builder.Services.AddScoped<IUserAdminService, UserAdminService>();
builder.Services.AddScoped<IAuditTrailService, AuditTrailService>();

builder.Services.AddDbContext<AuthDbContext>(options =>
    options.UseSqlServer(connectionString,
        sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", AuthDbContext.Schema)));

// ASP.NET Core Identity - application-managed credentials (NFR-03).
// Accounts are created by the Managing Director; there is no self-registration.
builder.Services
    .AddIdentity<UserAccount, IdentityRole>(options =>
    {
        // Length over composition rules, following NIST SP 800-63B (Task 1 8.2).
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredUniqueChars = 4;

        // Lockout (NFR-03). Only takes effect when sign-in passes lockoutOnFailure: true.
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;

        options.User.RequireUniqueEmail = true;

        // No email service exists (client decision), so accounts are not confirmed by email.
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<AuthDbContext>()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>();

// Session cookie hardening. Estimators work from laptops on networks we do not
// control (NFR-12), so the session has a short idle timeout.
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "TechnoSurfaces.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromHours(1);
    options.SlidingExpiration = true;
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";

    // A browser page is sent to sign-in or to the access-denied note. A call to
    // /api is answered with 401 or 403 instead, so the costing sheet's script sees
    // the real status rather than following a redirect to an HTML page.
    options.Events.OnRedirectToLogin = context => ApiAwareRedirect(context, StatusCodes.Status401Unauthorized);
    options.Events.OnRedirectToAccessDenied = context => ApiAwareRedirect(context, StatusCodes.Status403Forbidden);

    static Task ApiAwareRedirect(
        Microsoft.AspNetCore.Authentication.RedirectContext<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions> context,
        int apiStatus)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
            context.Response.StatusCode = apiStatus;
        else
            context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }
});

// RFC 9457 problem details for every API error.
builder.Services.AddProblemDetails();

// Re-validate the security stamp every minute, so a deactivated user's open
// session ends within a minute rather than when the cookie expires.
builder.Services.Configure<SecurityStampValidatorOptions>(options =>
    options.ValidationInterval = TimeSpan.FromMinutes(1));

// Every state-changing request must carry an antiforgery token (Task 1 8.8), and a
// user on a temporary password can reach nothing but "Set your password" (8.2).
// Both are global, so a new page cannot forget them.
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    options.Filters.Add<MustChangePasswordFilter>();
});
// US-27: no page is reachable without an authenticated session. Anything not
// explicitly marked [AllowAnonymous] requires sign-in, so a forgotten attribute
// fails closed rather than open.
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    // The capability matrix (Task 1 2.2), defined once. Estimators may view all
    // pricing - a confirmed client decision - so viewing needs no policy.
    options.AddPolicy(Policies.CanApproveQuote, p => p.RequireRole(Roles.ManagingDirector));
    options.AddPolicy(Policies.CanEditCatalogue, p => p.RequireRole(Roles.ManagingDirector));
    options.AddPolicy(Policies.CanManageUsers, p => p.RequireRole(Roles.ManagingDirector));
    options.AddPolicy(Policies.CanViewAuditTrail, p => p.RequireRole(Roles.ManagingDirector));
    options.AddPolicy(Policies.CanEditQuote, p => p.AddRequirements(new EditQuoteRequirement()));
    options.AddPolicy(Policies.CanReopenQuote, p => p.AddRequirements(new ReopenQuoteRequirement()));

    // Recording the Pastel invoice is for the Managing Director only (US-25, team decision).
    options.AddPolicy(Policies.CanRecordInvoice, p => p.RequireRole(Roles.ManagingDirector));
});
builder.Services.AddSingleton<IAuthorizationHandler, EditQuoteHandler>();
builder.Services.AddSingleton<IAuthorizationHandler, ReopenQuoteHandler>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ISignInService, SignInService>();
builder.Services.AddScoped<TechnoSurfaces.Services.DemoSession>();
builder.Services.AddPlatformHealthChecks();
builder.Services.AddSignInRateLimiting();


var app = builder.Build();

// Bring both databases up to date and load the catalogue and rate card: always on
// a developer machine, and in Azure when Database__MigrateOnStartup is true.
await DatabaseStartup.InitialiseAsync(app);

// Roles in every environment. The demo accounts on developer machines, and in a
// non-production Azure environment only where Seed__DemoAccounts is true. They are
// never created in Production, whatever the setting says.
await IdentitySeeder.SeedAsync(app.Services, app.Configuration, app.Logger,
    includeDevelopmentAccounts: app.Environment.IsDevelopment()
        || (app.Configuration.GetValue<bool>("Seed:DemoAccounts") && !app.Environment.IsProduction()));

// Load the in-memory demo data the prototype screens still read from.
TechnoSurfacesApp.Data.Db.Initialise();

// Configure the HTTP request pipeline.
app.UseSecurityHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// An unexpected error on /api is answered with a ProblemDetails body, not the HTML
// error page the browser screens use, so the costing sheet's script can read it.
// Registered after the page handler so it is the inner one and runs first.
app.UseWhen(context => context.Request.Path.StartsWithSegments("/api"),
    api => api.UseExceptionHandler());

app.UseHttpsRedirection();
app.UseRouting();
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// CSS, scripts and the logo hold no data and must load on the sign-in page.
app.MapStaticAssets().AllowAnonymous();
app.MapPlatformHealthChecks();


app.MapControllerRoute(
    name: "default",
        pattern: "{controller=Account}/{action=Login}/{id?}");


app.Run();

// Lets the integration tests start the app through WebApplicationFactory<Program>.
public partial class Program { }