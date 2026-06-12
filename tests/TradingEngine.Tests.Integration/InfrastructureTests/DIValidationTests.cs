namespace TradingEngine.Tests.Integration.InfrastructureTests;

public sealed class DIValidationTests
{
    [Fact]
    public void AllCoreServices_RegisteredAndResolvable()
    {
        var solRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var services = new ServiceCollection();
        services.AddLogging();

        // Infrastructure substitutes
        services.AddSingleton<IBrokerAdapter>(_ => Substitute.For<IBrokerAdapter>());
        services.AddSingleton(new EngineRunContext("di-test"));

        // Minimal services needed for the container
        var symbolCatalog = new SymbolCatalog(solRoot);
        var symbols = symbolCatalog.GetAll();
        var symbolRegistry = new SymbolInfoRegistry();
        foreach (var si in symbols) symbolRegistry.Register(si);
        services.AddSingleton<ISymbolInfoRegistry>(symbolRegistry);
        services.AddSingleton<Func<string, string, decimal>>(_ => (_, _) => 1m);
        services.AddSingleton(new CrossRateStore());
        services.AddSingleton<IEngineClock, BrokerClock>();
        services.AddSingleton<INewsFilter>(_ => new ConfigurableNewsFilter([]));
        services.AddSingleton<SessionFilter>();
        services.AddSingleton<DrawdownTracker>();
        services.AddSingleton<ICurrencyExposureTracker, CurrencyExposureTracker>();
        services.AddSingleton<RiskManager>();
        services.AddSingleton<IRiskManager>(sp => sp.GetRequiredService<RiskManager>());
        services.AddSingleton<IPassProbabilityEstimator, PassProbabilityEstimator>();
        services.AddSingleton<IRiskProfileResolver>(_ => new RiskProfileResolver([new RiskProfile("s", "S", 0.01, 0.04, 0.08, 100, 0.05, 0.5, 0.5, 3, false, "ftmo")]));

        // Size modifiers
        services.AddSingleton<ISizeModifier, DrawdownSizeModifier>();
        services.AddSingleton<ISizeModifier, AtrRegimeSizeModifier>();
        services.AddSingleton<ISizeModifier, TimeOfDaySizeModifier>();
        services.AddSingleton<ISizeModifier, ConfidenceSizeModifier>();
        services.AddSingleton<SizeModifierPipeline>();

        // Strategy infrastructure
        services.AddSingleton<IIndicatorService, SkenderIndicatorService>();
        services.AddSingleton<IRegimeDetector, AtrBasedRegimeDetector>();
        var registry = new StrategyRegistry();
        services.AddSingleton(registry);
        services.AddSingleton<IStrategyBank>(sp => new StrategyBankService(registry, null, sp.GetRequiredService<ILogger<StrategyBankService>>()));

        // Trading services
        services.AddSingleton<IPositionManager, PositionManager>();
        services.AddSingleton<IEventBus, TypedEventBus>();
        services.AddSingleton<OrderDispatcher>();
        services.AddSingleton<PositionTracker>();

        var sp = services.BuildServiceProvider();
        var failures = new List<string>();

        AssertResolves<IRiskManager>(sp, failures);
        AssertResolves<DrawdownTracker>(sp, failures);
        AssertResolves<IStrategyBank>(sp, failures);
        AssertResolves<IRegimeDetector>(sp, failures);
        AssertResolves<ICurrencyExposureTracker>(sp, failures);
        AssertResolves<INewsFilter>(sp, failures);
        AssertResolves<IPassProbabilityEstimator>(sp, failures);
        AssertResolves<SizeModifierPipeline>(sp, failures);
        AssertResolves<OrderDispatcher>(sp, failures);
        AssertResolves<PositionTracker>(sp, failures);
        AssertResolves<IIndicatorService>(sp, failures);
        AssertResolves<IEventBus>(sp, failures);
        AssertResolves<IPositionManager>(sp, failures);

        failures.Should().BeEmpty(string.Join("\n", failures));
    }

    private static void AssertResolves<T>(IServiceProvider sp, List<string> failures)
    {
        try { var svc = sp.GetRequiredService(typeof(T)); if (svc is null) throw new Exception("resolved null"); }
        catch (Exception ex) { failures.Add($"  {typeof(T).Name}: {ex.Message}"); }
    }
}
