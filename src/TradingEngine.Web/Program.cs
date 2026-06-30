using Serilog;
using TradingEngine.Web.Configuration;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Host.UseSerilog();

builder.Services.AddShamshir(builder.Configuration);

var app = builder.Build();
await app.UseShamshir();

try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Web host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program { }
