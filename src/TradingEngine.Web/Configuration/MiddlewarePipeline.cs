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
