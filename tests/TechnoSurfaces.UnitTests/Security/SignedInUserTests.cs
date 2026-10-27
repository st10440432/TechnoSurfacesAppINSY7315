using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.People;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfacesApp.Identity;
using TechnoSurfacesApp.Services;

namespace TechnoSurfaces.UnitTests.Security;

/// <summary>
/// The person shown in the side menu on every page. Their name travels in the session,
/// so a page does not look it up (NFR-05); a session issued before that still shows it.
/// </summary>
public sealed class SignedInUserTests : IAsyncLifetime
{
    private const string UserId = "user-lerato";

    private SqliteConnection _connection = null!;
    private TechnoSurfacesDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new TechnoSurfacesDbContext(
            new DbContextOptionsBuilder<TechnoSurfacesDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();

        _db.Users.Add(new AppUser
        {
            Id = UserId, UserName = "lerato@technosurfaces.co.za", Email = "lerato@technosurfaces.co.za",
            FullName = "Lerato Mokoena", Role = UserRole.Estimator
        });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static IHttpContextAccessor SignedIn(params Claim[] extra)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, UserId),
            new(ClaimTypes.Name, "lerato@technosurfaces.co.za"),
            new(ClaimTypes.Role, Roles.Estimator)
        };
        claims.AddRange(extra);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        return new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } };
    }

    [Fact]
    public async Task The_name_comes_from_the_session_without_a_query()
    {
        // A database with no tables: any query would throw.
        await using var empty = new SqliteConnection("DataSource=:memory:");
        empty.Open();
        await using var noTables = new TechnoSurfacesDbContext(
            new DbContextOptionsBuilder<TechnoSurfacesDbContext>().UseSqlite(empty).Options);

        var me = new SignedInUser(SignedIn(new Claim(AppClaimsPrincipalFactory.FullNameClaim, "Lerato Mokoena")), noTables);
        var profile = await me.GetAsync();

        Assert.Equal("Lerato Mokoena", profile.FullName);
        Assert.Equal("LM", profile.Initials);
        Assert.Equal("lerato@technosurfaces.co.za", profile.Email);
        Assert.Equal("Estimator", profile.RoleLabel);
    }

    [Fact]
    public async Task A_session_without_the_name_still_shows_it_from_the_database()
    {
        var me = new SignedInUser(SignedIn(), _db);
        var profile = await me.GetAsync();

        Assert.Equal("Lerato Mokoena", profile.FullName);
        Assert.Equal("LM", profile.Initials);
    }
}
