using Dragonmind.Agents.Knowledge;
using Dragonmind.Agents.Scripted;

using Dragonmind.Core.AI;
using Dragonmind.Core.Application.AntiCorruptionLayer;
using Dragonmind.Core.Domain.SharedIdentities;

using Dragonmind.Knowledge.Infrastructure.DI;
using Dragonmind.Knowledge.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace Dragonmind.Agents.IntegrationTests;

/// <summary>
/// Brings up the real knowledge layer from the submodule against a real PostgreSQL with pgvector and
/// Apache AGE, wired exactly the way a host application wires it.
/// </summary>
/// <remarks>
/// <para>
/// The connection string comes from <c>STEWARD_TEST_CONNECTION</c> and from nowhere else. There is
/// no fallback and no skip path: a suite that quietly no-ops when the database is unreachable
/// reports green while proving nothing, which is worse than a suite that fails.
/// </para>
/// <para>
/// Two substitutions, and only two: a memory-backed <c>IDistributedCache</c>, and
/// <see cref="HashEmbeddingGenerator"/> in place of a model. Everything below the facade — the
/// commands, the repositories, the <c>vector(1536)</c> column, the HNSW index, the Cypher — is the
/// real thing.
/// </para>
/// </remarks>
public sealed class StewardFixture : IAsyncLifetime
{
    private const string ConnectionStringVariable = "STEWARD_TEST_CONNECTION";

    private ServiceProvider? _provider;

    /// <summary>The knowledge facade, as a consumer sees it.</summary>
    public IKnowledgeContextFacade Facade { get; private set; } = null!;

    /// <summary>The data source, for direct queries that check what actually landed.</summary>
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>Builds a scope-bound port over the facade, the way the host does per run.</summary>
    public IScopedKnowledge ScopedTo(ScopeId scope) => new ScopedKnowledge(Facade, scope);

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"{ConnectionStringVariable} is not set. These tests need a real PostgreSQL with pgvector " +
                "and Apache AGE - `docker compose up -d --wait db` brings one up. There is no in-memory " +
                "fallback and no skip path, deliberately: a green run has to mean the real stack worked.");
        }

        DataSource = KnowledgeDataSource.Create(connectionString);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDistributedMemoryCache();
        services.AddSingleton<IEmbeddingGenerator, HashEmbeddingGenerator>();
        services.AddKnowledgeContext(DataSource);

        _provider = services.BuildServiceProvider();

        await using (var context = await _provider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>()
                         .CreateDbContextAsync())
        {
            await context.Database.MigrateAsync();
        }

        Facade = _provider.GetRequiredService<IKnowledgeContextFacade>();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }
    }
}

/// <summary>
/// One fixture for every integration test class, so the database is migrated once.
/// </summary>
[CollectionDefinition(Name)]
public sealed class StewardCollection : ICollectionFixture<StewardFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "steward";
}
