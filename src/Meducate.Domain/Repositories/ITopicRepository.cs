using Meducate.Domain.Entities;

namespace Meducate.Domain.Repositories;

internal interface ITopicRepository
{
    Task<IEnumerable<HealthTopic>> GetAllAsync(int skip = 0, int take = 50, string? topicType = null, CancellationToken ct = default);
    Task<int> GetCountAsync(string? topicType = null, CancellationToken ct = default);
    Task<HealthTopic?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<IEnumerable<HealthTopic>> SearchAsync(string query, int skip = 0, int take = 50, string? topicType = null, CancellationToken ct = default);
    Task<int> SearchCountAsync(string query, string? topicType = null, CancellationToken ct = default);
    Task<IReadOnlyList<TopicTypeSummary>> GetDistinctTypesAsync(CancellationToken ct = default);
    void InvalidateCache();
}

internal sealed record TopicTypeSummary(string Type, int Count)
{
    public string Href => $"/api/topics?type={Uri.EscapeDataString(Type)}";
}
