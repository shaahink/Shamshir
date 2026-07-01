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

        // iter-38 PK1: seed the 3 reusable starter add-on packs (idempotent).
        var packLogger = scope.ServiceProvider.GetRequiredService<ILogger<AddOnPackSeeder>>();
        var packSeeder = new AddOnPackSeeder(
            scope.ServiceProvider.GetRequiredService<IAddOnPackStore>(), packLogger);
        await packSeeder.SeedAsync(default);

        // Single-origin hosting: ASP.NET serves the built Angular SPA (web-ui → wwwroot) alongside the
        // JSON API, the SignalR hub and the Scalar docs. One `dotnet run` gives the whole app — no
        // separate `ng serve`/proxy/port-4200 needed (that remains available for HMR via `npm start`).
        app.UseDefaultFiles();
        app.UseStaticFiles();

        // WebSocket middleware must run before UseRouting so Kestrel can intercept the
        // Upgrade: websocket header and hand off to SignalR's transport. Without this,
        // SignalR falls back to long-polling and the browser DevTools shows negotiate
        // stuck at "pending" because the fallback-to-file catch-all serves index.html
        // for /hubs/run/negotiate instead of the SignalR response.
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });

        // Catch unhandled exceptions from any downstream middleware/endpoint, log them to
        // Serilog (file + console), and return a 500 JSON so the frontend ErrorLogService
        // and scripts/check-errors.ps1 have a structured record of every crash.
        app.Use(async (ctx, next) =>
        {
            try { await next(); }
            catch (Exception ex)
            {
                var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
                logger.LogError(ex, "Unhandled exception on {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(
                    System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message, traceId = ctx.TraceIdentifier }));
            }
        });

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
