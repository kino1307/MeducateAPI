using Meducate.Domain.Entities;

namespace Meducate.Domain.Services;

internal interface ILLMProcessor
{
    Task<HealthTopic?> ParseHealthTopicAsync(string rawText, string? topicType = null, string? discoveredName = null, CancellationToken ct = default);
    Task<Dictionary<string, string>> ClassifyTopicNamesAsync(IReadOnlyList<TopicClassifyInput> topics, CancellationToken ct = default);
    Task<Dictionary<string, string>> ClassifyTopicCategoriesAsync(IReadOnlyList<TopicCategoryInput> topics, CancellationToken ct = default);
    Task<BroaderNameResult> CompareBroaderNameAsync(string candidate, string existing, CancellationToken ct = default);
    Task<Dictionary<string, string>> MatchOriginalNamesAsync(IReadOnlyList<string> normalizedNames, IReadOnlyList<string> candidateNames, CancellationToken ct = default);
    bool ShouldProcessTopicType(string? topicType);
}

internal interface ILLMProcessorLogger
{
    void LogSkippedTopic(string topicName, string reason);
    void LogInvalidClassification(string topicName, string invalidType);
    void LogInvalidCategoryPair(string topicName, string type, string category);
    void LogBatchError(string operation, int batchSize, Exception exception);
}

internal sealed record BroaderNameResult(string PreferredName, bool ShouldReplace);

internal sealed record TopicClassifyInput(string Name, string? SummarySnippet = null);

internal sealed record TopicCategoryInput(string Name, string TopicType, string? SummarySnippet = null);

