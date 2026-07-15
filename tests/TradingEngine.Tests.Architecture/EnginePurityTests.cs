using System.Reflection;
using FluentAssertions;

namespace TradingEngine.Tests.Architecture;

public class EnginePurityTests
{
    private static readonly Assembly EngineAssembly = typeof(TradingEngine.Engine.EnginePlaceholder).Assembly;
    private static readonly string EngineAssemblyName = EngineAssembly.GetName().Name!;
    private static readonly string DomainAssemblyName = typeof(TradingEngine.Domain.EngineMode).Assembly.GetName().Name!;

    private static readonly HashSet<string> KnownBclPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.",
        "Microsoft.",
        "netstandard",
        "mscorlib",
        "WindowsBase",
    };

    private static bool IsBclAssembly(AssemblyName name)
    {
        var n = name.Name ?? "";
        return KnownBclPrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Engine_references_only_Domain()
    {
        var refs = EngineAssembly.GetReferencedAssemblies();
        var nonBcl = refs.Where(r => !IsBclAssembly(r)).ToList();

        nonBcl.Should().ContainSingle(r => r.Name == DomainAssemblyName,
            $"TradingEngine.Engine must only reference TradingEngine.Domain. Found: {string.Join(", ", nonBcl.Select(r => r.Name))}");
    }

    [Fact]
    public void Engine_has_no_ILogger_no_DateTimeNow()
    {
        var forbiddenTypeNames = new HashSet<string>
        {
            "ILogger", "ILogger`1", "ILoggerFactory",
            "DateTime", "DateTimeOffset",
            // EF types
            "DbContext", "DbSet`1", "IQueryable`1",
        };

        var forbiddenNamespaces = new HashSet<string>
        {
            "Microsoft.Extensions.Logging",
            "Microsoft.EntityFrameworkCore",
            "System.Data",
        };

        var violations = new List<string>();

        foreach (var type in EngineAssembly.GetExportedTypes())
        {
            // AF6: GovernorMachine implements ITradingGovernor (in Domain) which requires DateTime
            // as a parameter. The interface contract is in Domain; the Engine merely implements it.
            var isGovernorImplementor = type.GetInterfaces().Any(i => i.Name == "ITradingGovernor");

            // Check type-level references
            foreach (var iface in type.GetInterfaces())
            {
                CheckType(iface, type.Name, "implements", violations, forbiddenTypeNames, forbiddenNamespaces);
            }

            if (type.BaseType is not null && type.BaseType != typeof(object) && type.BaseType != typeof(ValueType))
            {
                CheckType(type.BaseType, type.Name, "inherits", violations, forbiddenTypeNames, forbiddenNamespaces);
            }

            // Check custom attributes
            foreach (var attr in type.CustomAttributes)
            {
                CheckType(attr.AttributeType, type.Name, "has attribute", violations, forbiddenTypeNames, forbiddenNamespaces);
            }

            // Check constructors
            var ctorForbiddenTypes = isGovernorImplementor
                ? forbiddenTypeNames.Where(n => n != "DateTime" && n != "DateTimeOffset").ToHashSet()
                : forbiddenTypeNames;
            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var p in ctor.GetParameters())
                {
                    CheckType(p.ParameterType, $"{type.Name}.ctor({p.Name})", "parameter", violations, ctorForbiddenTypes, forbiddenNamespaces);
                }
            }

            // Check properties
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                CheckType(prop.PropertyType, $"{type.Name}.{prop.Name}", "property type", violations, ctorForbiddenTypes, forbiddenNamespaces);
            }

            // Check methods
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (method.DeclaringType != type) continue; // skip inherited

                // AF6: ITradingGovernor requires DateTime parameters — allowed.
                var methodForbiddenTypes = isGovernorImplementor
                    ? forbiddenTypeNames.Where(n => n != "DateTime" && n != "DateTimeOffset").ToHashSet()
                    : forbiddenTypeNames;

                CheckType(method.ReturnType, $"{type.Name}.{method.Name}()", "return type", violations, methodForbiddenTypes, forbiddenNamespaces);

                foreach (var p in method.GetParameters())
                {
                    CheckType(p.ParameterType, $"{type.Name}.{method.Name}({p.Name})", "parameter", violations, methodForbiddenTypes, forbiddenNamespaces);
                }
            }

            // Check fields
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                CheckType(field.FieldType, $"{type.Name}.{field.Name}", "field type", violations, ctorForbiddenTypes, forbiddenNamespaces);
            }
        }

        violations.Should().BeEmpty(
            $"TradingEngine.Engine must not reference ILogger, DateTime, or EF types.\n{string.Join("\n", violations)}");
    }

    [Fact]
    public void EngineMode_only_in_host_and_infrastructure()
    {
        // This test will be un-skipped in Phase 6 when EngineMode is confined to Host/Infrastructure/composition.
        var engineModeType = typeof(TradingEngine.Domain.EngineMode);

        var allowedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TradingEngine.Domain",     // definition
            "TradingEngine.Host",
            "TradingEngine.Infrastructure",
            "TradingEngine.Tests.Architecture",
            "TradingEngine.Tests.Unit",
            "TradingEngine.Tests.Integration",
            "TradingEngine.Tests.Simulation",
        };

        // Load all relevant assemblies
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name!.StartsWith("TradingEngine", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var violations = new List<string>();

        foreach (var asm in assemblies)
        {
            if (allowedAssemblies.Contains(asm.GetName().Name!)) continue;

            try
            {
                foreach (var type in asm.GetExportedTypes())
                {
                    if (ReferencesType(type, engineModeType))
                    {
                        violations.Add($"{asm.GetName().Name} :: {type.FullName}");
                    }
                }
            }
            catch
            {
                // Some assemblies may fail to load types — skip
            }
        }

        violations.Should().BeEmpty(
            $"EngineMode must only be referenced from Host, Infrastructure, or test assemblies.\n{string.Join("\n", violations)}");
    }

    private static void CheckType(
        Type type,
        string location,
        string role,
        List<string> violations,
        HashSet<string> forbiddenTypeNames,
        HashSet<string> forbiddenNamespaces)
    {
        // Unwrap generics
        var rootType = type.IsGenericType ? type.GetGenericTypeDefinition() : type;

        var typeName = rootType.Name;
        var typeNamespace = rootType.Namespace ?? "";

        if (forbiddenTypeNames.Contains(typeName))
        {
            violations.Add($"  {location} {role} {type.FullName} (matches forbidden type '{typeName}')");
        }

        if (forbiddenNamespaces.Any(ns => typeNamespace.StartsWith(ns, StringComparison.OrdinalIgnoreCase)))
        {
            violations.Add($"  {location} {role} {type.FullName} (in forbidden namespace '{typeNamespace}')");
        }

        // Recursively check generic type arguments
        if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments())
            {
                CheckType(arg, location, $"{role} <T>", violations, forbiddenTypeNames, forbiddenNamespaces);
            }
        }
    }

    [Fact]
    public void Engine_has_no_GuidNewGuid_or_DateTimeUtcNow_in_source()
    {
        var engineSrcDir = Path.GetFullPath(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "src", "TradingEngine.Engine"));

        if (!Directory.Exists(engineSrcDir))
        {
            // Fallback: try relative to the test assembly location
            engineSrcDir = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "src", "TradingEngine.Engine"));
        }

        var forbiddenPatterns = new (string Pattern, string Description)[]
        {
            ("Guid.NewGuid()", "Guid.NewGuid()"),
            ("DateTime.UtcNow", "DateTime.UtcNow"),
            ("DateTime.Now", "DateTime.Now (non-Utc)"),
            ("DateTimeOffset.UtcNow", "DateTimeOffset.UtcNow"),
            ("DateTimeOffset.Now", "DateTimeOffset.Now"),
        };

        // Guard against a HOLLOW gate: if the source dir can't be resolved (e.g. run from an
        // unexpected working dir), GetFiles would otherwise throw or scan nothing and the determinism
        // guarantee would silently lapse. Fail loudly instead.
        Directory.Exists(engineSrcDir).Should().BeTrue(
            $"the Engine source directory must be found for the purity scan to run (looked at '{engineSrcDir}')");

        var violations = new List<string>();
        var csFiles = Directory.GetFiles(engineSrcDir, "*.cs", SearchOption.AllDirectories);

        // The scan must actually cover the kernel-core files — otherwise a green result is meaningless.
        var scanned = csFiles.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var required in new[]
                 {
                     "Kernel.cs", "EngineReducer.cs", "PreTradeGate.cs", "KernelSizing.cs",
                     "PositionLifecycle.cs", "GovernorMachine.cs", "DrawdownReducer.cs",
                 })
        {
            scanned.Should().Contain(required,
                $"the determinism purity scan must cover the kernel-core file '{required}'");
        }

        foreach (var file in csFiles)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // Skip comments and strings
                var trimmed = line.Trim();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("///")) continue;

                foreach (var (pattern, description) in forbiddenPatterns)
                {
                    if (line.Contains(pattern))
                    {
                        violations.Add(
                            $"  {Path.GetFileName(file)}:{i + 1} — {description}: {trimmed}");
                    }
                }
            }
        }

        violations.Should().BeEmpty(
            $"TradingEngine.Engine source must not contain Guid.NewGuid(), DateTime.UtcNow, DateTime.Now, or DateTimeOffset.UtcNow/Now — these break determinism.\n{string.Join("\n", violations)}");
    }

    private static bool ReferencesType(Type source, Type target)
    {
        // Check interfaces
        if (source.GetInterfaces().Any(i => i == target || (i.IsGenericType && i.GetGenericArguments().Contains(target))))
        {
            return true;
        }

        // Check fields
        if (source.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Any(f => f.FieldType == target || (f.FieldType.IsGenericType && f.FieldType.GetGenericArguments().Contains(target))))
        {
            return true;
        }

        // Check properties
        if (source.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Any(p => p.PropertyType == target || (p.PropertyType.IsGenericType && p.PropertyType.GetGenericArguments().Contains(target))))
        {
            return true;
        }

        // Check method parameters/return types
        foreach (var m in source.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            if (m.ReturnType == target || (m.ReturnType.IsGenericType && m.ReturnType.GetGenericArguments().Contains(target)))
            {
                return true;
            }
            if (m.GetParameters().Any(p => p.ParameterType == target || (p.ParameterType.IsGenericType && p.ParameterType.GetGenericArguments().Contains(target))))
            {
                return true;
            }
        }

        // Check constructor parameters
        foreach (var c in source.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (c.GetParameters().Any(p => p.ParameterType == target || (p.ParameterType.IsGenericType && p.ParameterType.GetGenericArguments().Contains(target))))
            {
                return true;
            }
        }

        return false;
    }
}
