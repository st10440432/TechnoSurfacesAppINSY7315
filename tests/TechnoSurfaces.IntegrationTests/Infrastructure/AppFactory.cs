using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;

namespace TechnoSurfaces.IntegrationTests.Infrastructure;

/// <summary>
/// Starts the real application against a real SQL Server database created for this
/// test run, then drops the database when the run ends.
///
/// The app starts the way it does in Azure: environment "Testing" (not Development),
/// with Database__MigrateOnStartup and Seed__DemoAccounts switched on. So every run
/// proves that a fresh database migrates, seeds the catalogue, rate card and terms,
/// and accepts the demo accounts.
///
/// SQL Server comes from TECHNOSURFACES_TEST_SQL: a connection string with no
/// database name. CI sets it to the SQL Server service container. On a Windows
/// machine it defaults to LocalDB.
///
/// Settings are passed as environment variables because Program.cs reads the
/// connection string before WebApplicationFactory configuration callbacks run.
/// That is also why every test class shares this one fixture through the
/// "Integration" collection, and the collection does not run in parallel.
/// </summary>
public sealed class AppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string DemoPassword = "IntegrationTest2026";
    public const string ManagingDirectorEmail = "paul@technosurfaces.co.za";
    public const string EstimatorEmail = "lerato@technosurfaces.co.za";

    private const string LocalDb =
        @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _server;
    private readonly string _databaseName = "TechnoSurfaces_Test_" + Guid.NewGuid().ToString("N")[..8];

    public AppFactory()
    {
        _server = Environment.GetEnvironmentVariable("TECHNOSURFACES_TEST_SQL") ?? LocalDb;
        ConnectionString = new SqlConnectionStringBuilder(_server) { InitialCatalog = _databaseName }.ConnectionString;

        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", ConnectionString);
        Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "true");
        Environment.SetEnvironmentVariable("Seed__DemoAccounts", "true");
        Environment.SetEnvironmentVariable("Seed__DevelopmentPassword", DemoPassword);
    }

    public string ConnectionString { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseEnvironment("Testing");

    /// <summary>
    /// A client that keeps cookies and does not follow redirects, so a test can
    /// assert on the redirect itself. The address is https because the
    /// authentication cookie is Secure and would not be sent back over http.
    /// </summary>
    public HttpClient CreateBrowser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
        BaseAddress = new Uri("https://localhost")
    });

    public Task InitializeAsync()
    {
        // Starting the server runs the migrations and the seeding.
        _ = Server;
        return Task.CompletedTask;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();

        SqlConnection.ClearAllPools();
        var masterConnection = new SqlConnectionStringBuilder(_server) { InitialCatalog = "master" }.ConnectionString;
        await using var master = new SqlConnection(masterConnection);
        await master.OpenAsync();
        await using var drop = master.CreateCommand();
        drop.CommandText =
            $"IF DB_ID('{_databaseName}') IS NOT NULL BEGIN " +
            $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{_databaseName}]; END";
        await drop.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IntegrationCollection : ICollectionFixture<AppFactory>
{
    public const string Name = "Integration";
}