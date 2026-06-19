using Scalar.AspNetCore;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
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

        var rpLogger = scope.ServiceProvider.GetRequiredService<ILogger<RiskProfileSeeder>>();
        var rpSeeder = new RiskProfileSeeder(
            scope.ServiceProvider.GetRequiredService<IRiskProfileStore>(), rpLogger);
        await rpSeeder.SeedAsync(solRoot, default);

        var pfLogger = scope.ServiceProvider.GetRequiredService<ILogger<PropFirmRuleSetSeeder>>();
        var pfSeeder = new PropFirmRuleSetSeeder(
            scope.ServiceProvider.GetRequiredService<IPropFirmRuleSetStore>(), pfLogger);
        await pfSeeder.SeedAsync(solRoot, default);

        var govLogger = scope.ServiceProvider.GetRequiredService<ILogger<GovernorOptionsSeeder>>();
        var govSeeder = new GovernorOptionsSeeder(
            scope.ServiceProvider.GetRequiredService<IGovernorOptionsStore>(), govLogger);
        await govSeeder.SeedAsync(solRoot, default);

        // Single-origin hosting: ASP.NET serves the built Angular SPA (web-ui → wwwroot) alongside the
        // JSON API, the SignalR hub and the Scalar docs. One `dotnet run` gives the whole app — no
        // separate `ng serve`/proxy/port-4200 needed (that remains available for HMR via `npm start`).
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.UseCors();
        app.UseRouting();
        app.MapControllers();
        app.MapHub<RunHub>("/hubs/run");

        app.MapOpenApi();
        app.MapScalarApiReference();

        // SPA client-side routes (e.g. /runs/:id) fall through to index.html. API/hub/scalar routes are
        // matched first, so this only catches genuine client routes.
        app.MapFallbackToFile("index.html");
    }
}
