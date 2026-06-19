using TradingEngine.Web.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddShamshir(builder.Configuration);

var app = builder.Build();
await app.UseShamshir();
app.Run();

public partial class Program { }
