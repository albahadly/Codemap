using System.Collections.Concurrent;
using Codemap.Application.Abstractions;
using Codemap.Domain;

namespace Codemap.Infrastructure.Persistence;

/// <summary>Unpublished scan results, kept for the lifetime of the server process (registered singleton).</summary>
public sealed class InMemoryScanResultStore : IScanResultStore
{
    private readonly ConcurrentDictionary<Guid, TopologyGraph> _graphs = new();

    public void Add(TopologyGraph graph) => _graphs[graph.Id] = graph;

    public TopologyGraph? Get(Guid id) => _graphs.GetValueOrDefault(id);

    public IReadOnlyList<TopologyGraph> GetAll() => _graphs.Values.ToList();
}
