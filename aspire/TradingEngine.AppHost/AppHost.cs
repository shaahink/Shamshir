var builder = DistributedApplication.CreateBuilder(args);

var dbPath = Path.GetFullPath(
    Path.Combine(builder.AppHostDirectory, "..", "..", "data", "trading.db"));

var engine = builder.AddProject<Projects.TradingEngine_Host>("engine")
    .WithEnvironment("Engine__Mode", "Live")
    .WithEnvironment("Engine__Broker__NetMQ__DataPort", "15555")
    .WithEnvironment("Engine__Broker__NetMQ__CommandPort", "15556")
    .WithEnvironment("Persistence__DbPath", dbPath);

var web = builder.AddProject<Projects.TradingEngine_Web>("web")
    .WithEnvironment("Persistence__DbPath", dbPath)
    .WithEnvironment("Engine__Broker__NetMQ__DataPort", "15555")
    .WithEnvironment("Engine__Broker__NetMQ__CommandPort", "15556")
    .WithEndpoint(port: 5200, scheme: "http", name: "web-http");

builder.Build().Run();
