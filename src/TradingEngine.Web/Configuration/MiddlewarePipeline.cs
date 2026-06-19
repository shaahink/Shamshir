using Scalar.AspNetCore;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Web.Hubs;

namespace TradingEngine.Web.Configuration;

public static class MiddlewarePipeline
{
    public static async Task UseShamshir(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        await db.Database.MigrateAsync();

        var solRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<StrategyConfigSeeder>>();
        var seeder = new StrategyConfigSeeder(
            scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(), solRoot, logger);
        await seeder.SeedAsync();

        app.UseCors();
        app.UseRouting();
        app.MapControllers();
        app.MapHub<RunHub>("/hubs/run");

        app.MapOpenApi();
        app.MapScalarApiReference();
    }
}
