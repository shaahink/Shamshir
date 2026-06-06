var builder = DistributedApplication.CreateBuilder(args);

var engine = builder.AddProject<Projects.TradingEngine_Host>("engine")
    .WithEnvironment("ENGINE_MODE", "Backtest");

var web = builder.AddProject<Projects.TradingEngine_Web>("web")
    .WithReference(engine)
    .WithEndpoint(port: 5200, scheme: "http", name: "web-http");

builder.Build().Run();
