using Microsoft.Extensions.DependencyInjection;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Infrastructure.Persistence.Reporting;

namespace TradingEngine.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSqliteDataProvider(
        this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<TradingDbContext>(options =>
        {
            options.UseSqlite(connectionString);
            options.AddInterceptors(new AuditStampInterceptor());
        });

        services.AddDbContext<ReportingDbContext>(options =>
            options.UseSqlite(connectionString));

        services.AddScoped<ITradeRepository, SqliteTradeRepository>();
        services.AddScoped<IOrderRepository, SqliteOrderRepository>();
        services.AddScoped<IEquityRepository, SqliteEquityRepository>();
        services.AddScoped<IEventLogRepository, SqliteEventLogRepository>();
        services.AddScoped<IBarRepository, SqliteBarRepository>();
        services.AddScoped<IDatasetRepository, SqliteDatasetRepository>();
        services.AddScoped<IConfigSetRepository, SqliteConfigSetRepository>();
        services.AddScoped<IDataProvider, SqliteDataProvider>();

        services.AddScoped(_ =>
        {
            var conn = new SqliteConnection(connectionString);
            conn.Open();
            return conn;
        });
        services.AddScoped<TradeReportQueries>();

        return services;
    }

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services)
    {
        services.AddSingleton<ISymbolInfoRegistry, SymbolInfoRegistry>();
        return services;
    }
}
