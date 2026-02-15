using Meducate.Domain.Services;
using Microsoft.Extensions.Logging;

namespace Meducate.Infrastructure.LLM;

internal sealed class LLMProcessorLogger(ILogger<SemanticKernelLLMProcessor> logger) : ILLMProcessorLogger
{
    private readonly ILogger _logger = logger;

    public void LogSkippedTopic(string topicName, string reason) =>
        _logger.LogInformation("LLM skipped topic '{Topic}': {Reason}", topicName, reason);

    public void LogInvalidClassification(string topicName, string invalidType) =>
        _logger.LogWarning("LLM returned invalid classification '{Type}' for topic '{Topic}'", invalidType, topicName);

    public void LogInvalidCategoryPair(string topicName, string type, string category) =>
        _logger.LogWarning("LLM returned invalid category '{Category}' for topic '{Topic}' (type: {Type})", category, topicName, type);

    public void LogBatchError(string operation, int batchSize, Exception exception) =>
        _logger.LogError(exception, "LLM batch operation '{Operation}' failed for batch of {BatchSize} topics", operation, batchSize);
}
