using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TechnoSurfaces.Application.Catalogue;
using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Application.Customers;
using TechnoSurfaces.Application.Pricing;
using TechnoSurfaces.Application.Quoting;
using TechnoSurfaces.Infrastructure.Data;

namespace TechnoSurfaces.Infrastructure;

/// <summary>
/// Registers the data access, the pricing strategies and the calculation engine.
///
/// Call this from the web application's Program.cs. The connection string comes
/// from Azure Key Vault through the App Service managed identity; no credential is
/// held in source control or in plain configuration.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddTechnoSurfaces(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<TechnoSurfacesDbContext>(o => o.UseSqlServer(connectionString));

        services.AddScoped<ICatalogueReader, CatalogueReader>();
        services.AddScoped<IQuotationTermsReader, QuotationTermsReader>();
        services.AddScoped<IQuoteTermsRecorder, QuoteTermsRecorder>();

        // Both strategies are registered, and PriceResolver picks the one matching
        // the supplier's pricing scheme. A sixth supplier on a new scheme means
        // adding a strategy here, not editing the resolver.
        services.AddScoped<IPriceResolutionStrategy, BandPricedStrategy>();
        services.AddScoped<IPriceResolutionStrategy, ItemPricedStrategy>();

        services.AddScoped<IPriceResolver, PriceResolver>();
        services.AddScoped<IRateResolver, RateResolver>();
        services.AddScoped<IPriceHistory, PriceHistory>();
        services.AddScoped<IQuoteCalculationService, QuoteCalculationService>();

        // The costing sheet: quotes, their priced lines and the cascading choice.
        services.AddScoped<IQuoteRepository, QuoteRepository>();
        services.AddScoped<ICostingSheetService, CostingSheetService>();
        services.AddScoped<ICatalogueBrowser, CatalogueBrowser>();

        // Customers and their contacts.
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<ICustomerService, CustomerService>();

        // The quote workflow and the lists the screens show. One QuoteQueries per
        // request serves both interfaces, so lapsed quotes are expired on the same
        // context the workflow saves through.
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<QuoteQueries>();
        services.AddScoped<IQuoteQueries>(sp => sp.GetRequiredService<QuoteQueries>());
        services.AddScoped<ILapsedQuotes>(sp => sp.GetRequiredService<QuoteQueries>());
        services.AddScoped<IQuoteWorkflowService, QuoteWorkflowService>();
        services.AddScoped<IQuotationGenerationService, QuotationGenerationService>();

        return services;
    }
}
