var builder = DistributedApplication.CreateBuilder(args);

var dbPath = Path.GetFullPath(
    Path.Combine(builder.AppHostDirectory, "..", "..", "data", "trading.db"));

var engine = builder.AddProject<Projects.TradingEngine_Host>("engine")
    .WithEnvironment("Engine__Mode", "Backtest")
    .WithEnvironment("Persistence__DbPath", dbPath);

var web = builder.AddProject<Projects.TradingEngine_Web>("web")
    .WithEnvironment("Persistence__DbPath", dbPath)
    .WithEndpoint(port: 5200, scheme: "http", name: "web-http")
    .WaitForCompletion(engine);

builder.Build().Run();
