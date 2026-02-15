using Meducate.Domain.Entities;

namespace Meducate.Domain.Repositories;

internal interface ITopicRepository
{
    Task<IEnumerable<TopicListItem>> GetAllAsync(int skip = 0, int take = 50, string? topicType = null, CancellationToken ct = default);
    Task<int> GetCountAsync(string? topicType = null, CancellationToken ct = default);
    Task<HealthTopic?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<IEnumerable<TopicListItem>> SearchAsync(string query, int skip = 0, int take = 50, string? topicType = null, CancellationToken ct = default);
    Task<int> SearchCountAsync(string query, string? topicType = null, CancellationToken ct = default);
    Task<IReadOnlyList<TopicTypeSummary>> GetDistinctTypesAsync(CancellationToken ct = default);
    void InvalidateCache();
}

/// <summary>
/// Lightweight projection for topic listings — avoids loading JSONB columns.
/// </summary>
public sealed record TopicListItem(
    Guid Id,
    string Name,
    string? Summary,
    string? TopicType,
    string? Category,
    DateTime LastUpdated);

internal sealed record TopicTypeSummary(string Type, int Count)
{
    public string Href => $"/api/topics?type={Uri.EscapeDataString(Type)}";
}
