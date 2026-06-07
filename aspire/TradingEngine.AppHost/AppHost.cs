var builder = DistributedApplication.CreateBuilder(args);

var dbPath = Path.GetFullPath(
    Path.Combine(builder.AppHostDirectory, "..", "..", "data", "trading.db"));

const string pipeName = "trading-engine";

var engine = builder.AddProject<Projects.TradingEngine_Host>("engine")
    .WithEnvironment("Engine__Mode", "Live")
    .WithEnvironment("Engine__Broker__PipeName", pipeName)
    .WithEnvironment("Persistence__DbPath", dbPath);

var web = builder.AddProject<Projects.TradingEngine_Web>("web")
    .WithEnvironment("Persistence__DbPath", dbPath)
    .WithEnvironment("Engine__Broker__PipeName", pipeName)
    .WithEndpoint(port: 5200, scheme: "http", name: "web-http");

builder.Build().Run();
