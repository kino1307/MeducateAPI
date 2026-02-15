using Hangfire.Console;
using Hangfire.Server;
using Meducate.Domain.Entities;
using Meducate.Domain.Repositories;
using Meducate.Domain.Services;
using Microsoft.Extensions.Logging;

namespace Meducate.Application.Services;

internal sealed class TopicBackfillService(
    ITopicQueryRepository queryRepo,
    ITopicWriteRepository writeRepo,
    ILLMProcessor llmProcessor,
    ILogger<TopicBackfillService> logger)
{
    private readonly ITopicQueryRepository _queryRepo = queryRepo;
    private readonly ITopicWriteRepository _writeRepo = writeRepo;
    private readonly ILLMProcessor _llmProcessor = llmProcessor;
    private readonly ILogger<TopicBackfillService> _logger = logger;

    internal async Task<int> BackfillTopicTypesAsync(CancellationToken ct, PerformContext? console = null)
    {
        var unclassified = await _queryRepo.GetUnclassifiedTopicsAsync(ct);

        if (unclassified.Count == 0)
            return 0;

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Backfilling TopicType for {Count} unclassified/Other topics", unclassified.Count);

        console?.WriteLine($"Backfilling TopicType for {unclassified.Count} unclassified/Other topics...");

        try
        {
            var topics = unclassified
                .Select(c => new TopicClassifyInput(c.Name, c.Summary))
                .ToList();
            var typeMap = await _llmProcessor.ClassifyTopicNamesAsync(topics, ct);

            var nonMedical = new List<HealthTopic>();

            foreach (var topic in unclassified)
            {
                var newType = typeMap.GetValueOrDefault(topic.Name, TopicConstants.TopicTypeOther);

                if (string.Equals(newType, TopicConstants.TopicTypeNonMedical, StringComparison.OrdinalIgnoreCase))
                {
                    nonMedical.Add(topic);
                    continue;
                }

                topic.TopicType = newType;
            }

            if (nonMedical.Count > 0)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    var nonMedicalNames = string.Join(", ", nonMedical.Select(t => t.Name));
                    _logger.LogInformation("Removing {Count} non-medical topics: {Names}",
                        nonMedical.Count, nonMedicalNames);
                }
                console?.WriteLine($"Removing {nonMedical.Count} non-medical topics: {string.Join(", ", nonMedical.Select(t => t.Name))}");
                await _writeRepo.RemoveRangeAsync(nonMedical, ct);
            }

            await _writeRepo.SaveChangesAsync(ct);

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Backfilled {Reclassified} topics, removed {Removed} non-medical",
                    unclassified.Count - nonMedical.Count, nonMedical.Count);

            console?.WriteLine($"Backfilled {unclassified.Count - nonMedical.Count} topics, removed {nonMedical.Count} non-medical.");

            return unclassified.Count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to backfill TopicType — will retry next run");
            console?.WriteLine($"Backfill failed: {ex.Message}");

            // Revert tracked changes
            _writeRepo.RevertChanges(unclassified);

            return 0;
        }
    }

    internal async Task<int> BackfillOriginalNamesAsync(IReadOnlySet<string> allKnownNames, CancellationToken ct, PerformContext? console = null)
    {
        var topics = await _queryRepo.GetTopicsWithoutOriginalNameAsync(ct);

        if (topics.Count == 0)
            return 0;

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Backfilling OriginalName for {Count} topics", topics.Count);

        console?.WriteLine($"Backfilling OriginalName for {topics.Count} topics...");

        try
        {
            var trivialCount = 0;
            var llmCount = 0;
            var remaining = new List<HealthTopic>();

            // Trivial matches: topics whose Name IS in allKnownNames
            foreach (var topic in topics)
            {
                if (allKnownNames.Contains(topic.Name))
                {
                    topic.OriginalName = topic.Name;
                    trivialCount++;
                }
                else
                {
                    remaining.Add(topic);
                }
            }

            if (remaining.Count > 0)
            {
                // Build unmatched provider names = allKnownNames minus all topics' Name and OriginalName values
                var allTopicNames = await _queryRepo.GetAllTopicNamesAsync(ct);
                var originalNameMap = await _queryRepo.GetOriginalNameMappingsAsync(ct);

                var usedNames = new HashSet<string>(allTopicNames, StringComparer.OrdinalIgnoreCase);
                foreach (var orig in originalNameMap.Values)
                    usedNames.Add(orig);

                var unmatchedProviderNames = allKnownNames.Where(n => !usedNames.Contains(n)).ToArray();

                if (unmatchedProviderNames.Length > 0)
                {
                    var normalizedNames = remaining.Select(t => t.Name).ToList();
                    var matches = await _llmProcessor.MatchOriginalNamesAsync(normalizedNames, unmatchedProviderNames, ct);

                    foreach (var topic in remaining)
                    {
                        if (matches.TryGetValue(topic.Name, out var originalName))
                        {
                            topic.OriginalName = originalName;
                            llmCount++;
                        }
                    }
                }
            }

            if (trivialCount > 0 || llmCount > 0)
                await _writeRepo.SaveChangesAsync(ct);

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Backfilled OriginalName: {Trivial} trivial, {LLM} LLM-matched", trivialCount, llmCount);

            console?.WriteLine($"Backfilled OriginalName: {trivialCount} trivial, {llmCount} LLM-matched.");

            return trivialCount + llmCount;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to backfill OriginalName — will retry next run");
            console?.WriteLine($"OriginalName backfill failed: {ex.Message}");

            _writeRepo.RevertChanges(topics);

            return 0;
        }
    }

    internal async Task<int> BackfillCategoriesAsync(CancellationToken ct, PerformContext? console = null)
    {
        var uncategorized = await _queryRepo.GetUncategorizedTopicsAsync(ct);

        if (uncategorized.Count == 0)
            return 0;

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Backfilling categories for {Count} uncategorized topics", uncategorized.Count);

        console?.WriteLine($"Backfilling categories for {uncategorized.Count} uncategorized topics...");

        try
        {
            var inputs = uncategorized
                .Select(c => new TopicCategoryInput(c.Name, c.TopicType ?? TopicConstants.TopicTypeOther, c.Summary))
                .ToList();
            var categoryMap = await _llmProcessor.ClassifyTopicCategoriesAsync(inputs, ct);

            var assigned = 0;
            foreach (var topic in uncategorized)
            {
                if (categoryMap.TryGetValue(topic.Name, out var category))
                {
                    topic.Category = category;
                    assigned++;
                }
            }

            await _writeRepo.SaveChangesAsync(ct);

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Assigned categories to {Count} topics", assigned);

            console?.WriteLine($"Assigned categories to {assigned} topics.");

            return assigned;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to backfill categories — will retry next run");
            console?.WriteLine($"Category backfill failed: {ex.Message}");

            _writeRepo.RevertChanges(uncategorized);

            return 0;
        }
    }
}
