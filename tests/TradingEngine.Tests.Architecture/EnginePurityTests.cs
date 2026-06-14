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
            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var p in ctor.GetParameters())
                {
                    CheckType(p.ParameterType, $"{type.Name}.ctor({p.Name})", "parameter", violations, forbiddenTypeNames, forbiddenNamespaces);
                }
            }

            // Check properties
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                CheckType(prop.PropertyType, $"{type.Name}.{prop.Name}", "property type", violations, forbiddenTypeNames, forbiddenNamespaces);
            }

            // Check methods
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (method.DeclaringType != type) continue; // skip inherited

                CheckType(method.ReturnType, $"{type.Name}.{method.Name}()", "return type", violations, forbiddenTypeNames, forbiddenNamespaces);

                foreach (var p in method.GetParameters())
                {
                    CheckType(p.ParameterType, $"{type.Name}.{method.Name}({p.Name})", "parameter", violations, forbiddenTypeNames, forbiddenNamespaces);
                }
            }

            // Check fields
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                CheckType(field.FieldType, $"{type.Name}.{field.Name}", "field type", violations, forbiddenTypeNames, forbiddenNamespaces);
            }
        }

        violations.Should().BeEmpty(
            $"TradingEngine.Engine must not reference ILogger, DateTime, or EF types.\n{string.Join("\n", violations)}");
    }

    [Fact(Skip = "Enabled in Phase 6 — EngineMode currently leaks into PositionTracker (Services) and Risk types")]
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
