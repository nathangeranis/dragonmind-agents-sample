using System.Reflection;

using Dragonmind.Agents.Classification;
using Dragonmind.Agents.Handlers;
using Dragonmind.Agents.Routing;
using Dragonmind.Agents.Turn;

namespace Dragonmind.Agents.UnitTests.Architecture;

/// <summary>
/// Enforces, by reflection, the three separations the rest of this repository argues for. Each is
/// true today because of how the code is arranged; these exist so it stays true after an edit made
/// by someone who has not read the argument.
/// </summary>
/// <remarks>
/// A failure here is a boundary leak in the source, not a test to relax.
/// </remarks>
public class BoundaryTests
{
    private static readonly Assembly Agents = typeof(TurnOrchestrator).Assembly;

    /// <summary>
    /// Expands a type into everything reachable from it: array element types, and for a generic
    /// type the open definition plus each argument, recursively. Without this, a check on
    /// <c>Task&lt;IReadOnlyList&lt;SomeEntity&gt;&gt;</c> only ever inspects <c>Task</c>.
    /// </summary>
    private static IEnumerable<Type> Expand(Type type)
    {
        if (type.IsByRef || type.IsPointer || type.IsArray)
        {
            var element = type.GetElementType();

            if (element is not null)
            {
                foreach (var inner in Expand(element))
                {
                    yield return inner;
                }
            }

            yield break;
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            yield return type.GetGenericTypeDefinition();

            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var inner in Expand(argument))
                {
                    yield return inner;
                }
            }

            yield break;
        }

        yield return type;
    }

    private static string Namespace(Type type) => type.Namespace ?? string.Empty;

    // =========================================================================================
    // 1. No agent can name a knowledge-layer or persistence type.
    // =========================================================================================

    [Fact]
    public void TheAgentAssemblyNeverReferencesTheKnowledgeContext()
    {
        // The strongest form of the check, and the reason the project graph is arranged the way it
        // is: Dragonmind.Agents references Dragonmind.Core (the facade contract, ScopeId, the DTOs)
        // and stops there. Dragonmind.Knowledge - where EF Core, Npgsql and Pgvector live - is
        // referenced only by the composition root and the integration tests. An agent therefore
        // cannot name a persistence type, because the assembly holding them is not on its graph.
        var referenced = Agents
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToList();

        Assert.DoesNotContain(referenced, name => name.Contains("Dragonmind.Knowledge", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referenced, name => name.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referenced, name => name.StartsWith("Pgvector", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referenced, name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoPublicSignatureInTheAgentAssemblyMentionsAPersistenceType()
    {
        // Belt to the assembly check's braces: it catches a type reached through a shared
        // dependency rather than a direct reference.
        string[] forbidden = ["Microsoft.EntityFrameworkCore", "Npgsql", "Pgvector", "Dragonmind.Knowledge"];

        var violations = new List<string>();

        foreach (var type in Agents.GetTypes().Where(t => t.IsPublic))
        {
            foreach (var (member, memberType) in PublicSignatureTypes(type))
            {
                foreach (var reachable in Expand(memberType))
                {
                    if (forbidden.Any(prefix => Namespace(reachable).StartsWith(prefix, StringComparison.Ordinal)))
                    {
                        violations.Add($"{member} mentions {reachable.FullName}");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join("; ", violations));
    }

    private static IEnumerable<(string Member, Type Type)> PublicSignatureTypes(Type type)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var property in type.GetProperties(Flags))
        {
            yield return ($"{type.FullName}.{property.Name}", property.PropertyType);
        }

        foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            foreach (var parameter in ctor.GetParameters())
            {
                yield return ($"{type.FullName}(ctor)", parameter.ParameterType);
            }
        }

        foreach (var method in type.GetMethods(Flags).Where(m => !m.IsSpecialName))
        {
            foreach (var parameter in method.GetParameters())
            {
                yield return ($"{type.FullName}.{method.Name}", parameter.ParameterType);
            }

            yield return ($"{type.FullName}.{method.Name}", method.ReturnType);
        }
    }

    // =========================================================================================
    // 2. No handler depends on the classifier or on another handler.
    // =========================================================================================

    /// <summary>
    /// Checks constructor parameters and private instance fields — which is where a
    /// dependency-injected collaborator ends up — against the other handlers and the classifier.
    /// </summary>
    /// <remarks>
    /// The honest scope of this test: it catches compile-time coupling through dependency
    /// injection, which is how every collaborator in this repository is supplied. It does not catch
    /// a handler that constructs another with <c>new</c>, or reaches one through a service locator;
    /// proving that needs method-body inspection, which is disproportionate tooling for three
    /// handlers a reviewer can read in a couple of minutes. It is a regression net, not a
    /// substitute for reading them.
    /// </remarks>
    [Fact]
    public void NoHandlerDependsOnAnotherHandlerOrOnTheClassifier()
    {
        Type[] handlerContracts = [typeof(IExplainerAgent), typeof(IPolicyAgent), typeof(IStateAgent)];

        var implementations = Agents
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => handlerContracts.Any(c => c.IsAssignableFrom(t)))
            .ToList();

        // If this is empty the test is vacuous, which is worse than failing.
        Assert.Equal(3, implementations.Count);

        var violations = new List<string>();

        foreach (var implementation in implementations)
        {
            // A handler may implement its OWN contract; anything else in this set is a leak.
            var forbidden = handlerContracts
                .Where(c => !c.IsAssignableFrom(implementation))
                .Append(typeof(IIntentAgent))
                .ToList();

            var dependencies = implementation
                .GetConstructors()
                .SelectMany(c => c.GetParameters().Select(p => p.ParameterType))
                .Concat(implementation
                    .GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Select(f => f.FieldType))
                .SelectMany(Expand)
                .Distinct();

            violations.AddRange(
                dependencies
                    .Where(forbidden.Contains)
                    .Select(d => $"{implementation.Name} depends on {d.Name}"));
        }

        Assert.True(violations.Count == 0, string.Join("; ", violations));
    }

    [Fact]
    public void NoHandlerDependsOnTheRoutingPolicy()
    {
        // A handler that could consult the policy could decide for itself whether it should have
        // been called, which would make the table advisory rather than authoritative.
        var implementations = Agents
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(IExplainerAgent).IsAssignableFrom(t)
                     || typeof(IPolicyAgent).IsAssignableFrom(t)
                     || typeof(IStateAgent).IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(implementations);

        foreach (var implementation in implementations)
        {
            var dependencies = implementation
                .GetConstructors()
                .SelectMany(c => c.GetParameters().Select(p => p.ParameterType))
                .SelectMany(Expand);

            Assert.DoesNotContain(typeof(IRoutePolicy), dependencies);
        }
    }

    // =========================================================================================
    // 3. The classifier's output cannot name a handler.
    // =========================================================================================

    [Fact]
    public void NoClassificationTypeExposesAHandlerIdentity()
    {
        // The central claim of the repository, as a test. If a HandlerId could appear on a
        // classification result, the model could be prompted to fill it in, and the routing policy
        // would quietly become a suggestion.
        var classificationTypes = Agents
            .GetTypes()
            .Where(t => typeof(IntentResult).IsAssignableFrom(t) || typeof(IntentClassification).IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(classificationTypes);

        var violations = new List<string>();

        foreach (var type in classificationTypes)
        {
            foreach (var (member, memberType) in PublicSignatureTypes(type))
            {
                if (Expand(memberType).Any(t => t == typeof(HandlerId) || t == typeof(Route) || t == typeof(RouteRule)))
                {
                    violations.Add(member);
                }
            }
        }

        Assert.True(violations.Count == 0, "classification exposes routing: " + string.Join("; ", violations));
    }

    [Fact]
    public void TheClassifierIsNotGivenTheChangeWindow()
    {
        // The projection is what the classifier sees. The change window is deliberately not in it,
        // which is what makes "the same sentence routes two ways" a property of the system rather
        // than of the prompt.
        var projection = typeof(Fleet.StateProjection);

        var names = projection
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        Assert.NotEmpty(names);
        Assert.DoesNotContain(names, n => n.Contains("Window", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Change", StringComparison.OrdinalIgnoreCase));
    }
}
