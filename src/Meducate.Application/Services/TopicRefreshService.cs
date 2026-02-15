using Hangfire.Console;
using Hangfire.Server;
using Meducate.Application.Helpers;
using Meducate.Domain.Entities;
using Meducate.Domain.Repositories;
using Meducate.Domain.Services;
using Microsoft.Extensions.Logging;

namespace Meducate.Application.Services;

internal sealed class TopicRefreshService(
    IEnumerable<IMedicalDataProvider> providers,
    ITopicQueryRepository queryRepo,
    ITopicWriteRepository writeRepo,
    ILLMProcessor llmProcessor,
    ITopicRepository topicRepo,
    ILogger<TopicRefreshService> logger)
{
    private readonly IMedicalDataProvider[] _providers = [.. providers];
    private readonly ITopicQueryRepository _queryRepo = queryRepo;
    private readonly ITopicWriteRepository _writeRepo = writeRepo;
    private readonly ILLMProcessor _llmProcessor = llmProcessor;
    private readonly ITopicRepository _topicRepo = topicRepo;
    private readonly ILogger<TopicRefreshService> _logger = logger;

    private static readonly TimeSpan LlmThrottle = TimeSpan.FromMilliseconds(500);

    internal async Task RefreshAllAsync(PerformContext? console = null, CancellationToken ct = default)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Starting topic refresh — Phase 1: Provider refresh");
        }
        console?.WriteLine("Phase 1: Provider refresh");

        // Phase 1 — Fetch fresh RawSource from all providers for topics not yet refreshed today
        var today = DateTime.UtcNow.Date;
        var toRefresh = await _queryRepo.GetTopicsNeedingRefreshAsync(today, ct);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("{Count} topics need provider refresh today", toRefresh.Count);
        }
        console?.WriteLine($"{toRefresh.Count} topics need provider refresh today.");

        var refreshed = 0;
        var changed = 0;

        // Parallelize provider fetches with bounded concurrency.
        // Cancellations are captured as errors (not rethrown) so Task.WhenAll
        // completes normally and the apply loop can save all successful results.
        using var fetchSemaphore = new SemaphoreSlim(5);
        var fetchTasks = toRefresh.Select(async topic =>
        {
            await fetchSemaphore.WaitAsync(ct);
            try
            {
                var results = await FetchAllProvidersAsync(topic, ct);
                return (topic, results, error: (Exception?)null);
            }
            catch (Exception ex)
            {
                return (topic, results: (List<RawTopicData>?)null, error: ex);
            }
            finally
            {
                fetchSemaphore.Release();
            }
        }).ToList();

        var fetchResults = await Task.WhenAll(fetchTasks);

        // Apply changes sequentially (EF Core DbContext is not thread-safe)
        foreach (var (topic, results, error) in fetchResults)
        {
            if (error is not null)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(error, "Failed to refresh topic '{Name}' — will retry next run",
                        topic.Name);
                }
                console?.WriteLine($"  [{topic.Name}] Error: {error.Message}");
                continue;
            }

            if (results is null || results.Count == 0)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning("No providers returned data for '{Name}' — will retry next run",
                        topic.Name);
                }
                console?.WriteLine($"  [{topic.Name}] No providers returned data — will retry.");
                continue;
            }

            var newRawSource = TopicHelpers.BuildMergedRawSource(results);

            var newHash = ContentHasher.GetSourceHash(results, newRawSource);

            if (!string.Equals(topic.SourceHash, newHash, StringComparison.Ordinal))
            {
                topic.RawSource = newRawSource;
                topic.SourceHash = newHash;
                topic.NeedsLlmReprocessing = true;
                changed++;
            }
            else if (!string.Equals(topic.RawSource, newRawSource, StringComparison.Ordinal))
            {
                // PubMed supplement changed but MedlinePlus didn't — update stored
                // source without triggering LLM reprocessing.
                topic.RawSource = newRawSource;
            }

            topic.LastSourceRefresh = DateTime.UtcNow;
            refreshed++;

            if (refreshed % 25 == 0)
            {
                if (_writeRepo.HasChanges())
                    await _writeRepo.SaveChangesAsync(ct);
                console?.WriteLine($"  Refreshed {refreshed}/{toRefresh.Count}...");
            }
        }

        // Flush any remaining unsaved changes — use CancellationToken.None
        // so completed work is persisted even during shutdown
        if (_writeRepo.HasChanges())
            await _writeRepo.SaveChangesAsync(CancellationToken.None);

        console?.WriteLine($"Phase 1 complete: {refreshed} refreshed, {changed} have new source data.");

        ct.ThrowIfCancellationRequested();

        // Phase 2 — LLM reprocess topics whose RawSource changed (parallel with bounded concurrency)
        var toReprocess = await _queryRepo.GetTopicsNeedingReprocessingAsync(ct);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Phase 2: {Count} topics need LLM reprocessing", toReprocess.Count);
        }
        console?.WriteLine($"Phase 2: {toReprocess.Count} topics need LLM reprocessing.");

        var reprocessedCount = 0;

        // Parallelize LLM calls with bounded concurrency, then apply results sequentially.
        // Cancellations are captured as errors so completed work is not discarded.
        using var llmSemaphore = new SemaphoreSlim(3);
        var llmTasks = toReprocess.Select(async topic =>
        {
            if (string.IsNullOrWhiteSpace(topic.RawSource) || topic.RawSource.Length < TopicConstants.MinSourceLength)
                return (topic, structured: null, skip: true, error: null);

            await llmSemaphore.WaitAsync(ct);
            try
            {
                await Task.Delay(LlmThrottle, ct);
                var structured = await _llmProcessor.ParseHealthTopicAsync(topic.RawSource, topic.TopicType, topic.Name, ct);
                return (topic, structured, skip: false, error: (Exception?)null);
            }
            catch (Exception ex)
            {
                return (topic, structured: null, skip: false, error: ex);
            }
            finally
            {
                llmSemaphore.Release();
            }
        }).ToList();

        var llmResults = await Task.WhenAll(llmTasks);

        // Apply LLM results sequentially (EF Core DbContext is not thread-safe)
        foreach (var (topic, structured, skip, error) in llmResults)
        {
            if (skip)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Skipping '{Name}' — source too short to process", topic.Name);
                }
                topic.NeedsLlmReprocessing = false;
                continue;
            }

            if (error is not null)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(error, "Failed to reprocess topic '{Name}' — flag stays true for retry",
                        topic.Name);
                }
                console?.WriteLine($"  [{topic.Name}] Reprocess failed: {error.Message}");
                continue;
            }

            if (structured is null)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning("LLM returned null for '{Name}' (type: {Type}) — clearing reprocessing flag",
                        topic.Name, topic.TopicType);
                }
                console?.WriteLine($"  [{topic.Name}] LLM returned null — skipping (filtered type?).");
                topic.NeedsLlmReprocessing = false;
                continue;
            }

            try
            {
                // Only allow the LLM to fix casing, not rename the topic entirely
                if (string.Equals(topic.Name, structured.Name, StringComparison.OrdinalIgnoreCase))
                {
                    topic.Name = structured.Name;
                }
                else
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.LogWarning(
                            "LLM attempted to rename '{Original}' to '{Returned}' — keeping original name",
                            topic.Name, structured.Name);
                    }
                }

                topic.Summary = structured.Summary;
                topic.Observations = structured.Observations;
                topic.Factors = structured.Factors;
                topic.Actions = structured.Actions;
                topic.Citations = structured.Citations;
                // Preserve existing Category — ParseHealthTopicAsync always returns null
                // for Category; it gets classified separately in Phase 3
                if (structured.Category is not null)
                    topic.Category = structured.Category;
                topic.Tags = structured.Tags;
                // Preserve existing TopicType — don't overwrite with null
                topic.LastUpdated = DateTime.UtcNow;
                topic.Version++;

                var qualityIssue = TopicHelpers.CheckTopicQuality(topic);
                if (qualityIssue is not null)
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation("Topic '{Name}' still low quality after reprocessing: {Reason}", topic.Name, qualityIssue);
                    }
                    console?.WriteLine($"  [{topic.Name}] Reprocessed but still low quality — {qualityIssue}");
                    // Keep NeedsLlmReprocessing = true so it's retried when source data improves
                }
                else
                {
                    topic.NeedsLlmReprocessing = false;
                }

                reprocessedCount++;

                if (reprocessedCount % 10 == 0)
                    await _writeRepo.SaveChangesAsync(ct);

                console?.WriteLine($"  Reprocessed: {topic.Name}{(qualityIssue is not null ? " (flagged)" : "")}");
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(ex, "Failed to apply reprocessed topic '{Name}' — flag stays true for retry",
                        topic.Name);
                }
                console?.WriteLine($"  [{topic.Name}] Apply failed: {ex.Message}");
                _writeRepo.RevertChanges(topic);
            }
        }

        // Flush any remaining unsaved reprocessed topics — use CancellationToken.None
        // so completed LLM work is persisted even during shutdown
        if (_writeRepo.HasChanges())
            await _writeRepo.SaveChangesAsync(CancellationToken.None);

        ct.ThrowIfCancellationRequested();

        // Phase 3 — Classify categories for any topics that don't have one
        var uncategorized = await _queryRepo.GetUncategorizedTopicsAsync(ct);

        var categorizedCount = 0;
        if (uncategorized.Count > 0)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Phase 3: Classifying categories for {Count} uncategorized topics", uncategorized.Count);
            }
            console?.WriteLine($"Phase 3: Classifying categories for {uncategorized.Count} uncategorized topics...");

            try
            {
                var inputs = uncategorized
                    .Select(c => new TopicCategoryInput(c.Name, c.TopicType ?? TopicConstants.TopicTypeOther, c.Summary))
                    .ToList();
                var categoryMap = await _llmProcessor.ClassifyTopicCategoriesAsync(inputs, ct);

                foreach (var topic in uncategorized)
                {
                    if (categoryMap.TryGetValue(topic.Name, out var category))
                    {
                        topic.Category = category;
                        categorizedCount++;
                    }
                }

                await _writeRepo.SaveChangesAsync(ct);
                console?.WriteLine($"Assigned categories to {categorizedCount} topics.");
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(ex, "Category classification failed — will retry next run");
                }
                console?.WriteLine($"Category classification failed: {ex.Message}");
                _writeRepo.RevertChanges(uncategorized);
            }
        }

        if (reprocessedCount > 0 || categorizedCount > 0)
        {
            _topicRepo.InvalidateCache();
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Cache invalidated after reprocessing {Reprocessed} and categorizing {Categorized} topics",
                    reprocessedCount, categorizedCount);
            }
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Topic refresh complete — refreshed {Refreshed}, reprocessed {Reprocessed}, categorized {Categorized}",
                toRefresh.Count, reprocessedCount, categorizedCount);
        }
        console?.WriteLine($"Done — {refreshed} refreshed, {reprocessedCount} reprocessed, {categorizedCount} categorized.");
    }

    private async Task<List<RawTopicData>> FetchAllProvidersAsync(HealthTopic topic, CancellationToken ct)
    {
        var tasks = _providers.Select(p => FetchSafeWithFallbackAsync(p, topic, ct));
        var results = await Task.WhenAll(tasks);
        return [.. results.OfType<RawTopicData>()];
    }

    private async Task<RawTopicData?> FetchSafeWithFallbackAsync(
        IMedicalDataProvider provider, HealthTopic topic, CancellationToken ct)
    {
        try
        {
            // Try OriginalName first (exact match for MedlinePlus)
            if (topic.OriginalName is not null)
            {
                var result = await provider.FetchTopicDataAsync(topic.OriginalName, ct);
                if (result is not null) return result;
            }

            // Fallback to Name if different from OriginalName
            if (topic.OriginalName is null
                || !string.Equals(topic.Name, topic.OriginalName, StringComparison.OrdinalIgnoreCase))
            {
                return await provider.FetchTopicDataAsync(topic.Name, ct);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Provider {Source} failed to fetch for '{Topic}'",
                provider.SourceName, topic.Name);
            return null;
        }
    }
}
