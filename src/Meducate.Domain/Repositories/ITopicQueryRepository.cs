using Meducate.Domain.Entities;

namespace Meducate.Domain.Repositories;

internal interface ITopicQueryRepository
{
    Task<List<string>> GetAllTopicNamesAsync(CancellationToken ct);
    Task<List<HealthTopic>> GetTopicsNeedingRefreshAsync(DateTime cutoff, CancellationToken ct);
    Task<List<HealthTopic>> GetTopicsNeedingReprocessingAsync(CancellationToken ct);
    Task<List<HealthTopic>> GetUncategorizedTopicsAsync(CancellationToken ct);
    Task<List<HealthTopic>> GetUnclassifiedTopicsAsync(CancellationToken ct);
    Task<HealthTopic?> GetByNameTrackedAsync(string name, CancellationToken ct);
    Task<List<HealthTopic>> GetByNamesTrackedAsync(IEnumerable<string> names, CancellationToken ct);
    Task<List<HealthTopic>> GetTopicsWithoutOriginalNameAsync(CancellationToken ct);
    Task<Dictionary<string, string>> GetOriginalNameMappingsAsync(CancellationToken ct);
    Task<HashSet<string>> GetAllSeenTopicNamesAsync(CancellationToken ct);
}
