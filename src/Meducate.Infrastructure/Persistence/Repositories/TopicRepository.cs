using Meducate.Domain.Entities;
using Meducate.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace Meducate.Infrastructure.Persistence.Repositories;

internal sealed class TopicRepository(MeducateDbContext context, IMemoryCache cache) : ITopicRepository
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan NegativeCacheDuration = TimeSpan.FromMinutes(2);
    private static CancellationTokenSource _cacheTokenSource = new();

    private static MemoryCacheEntryOptions CreateEntryOptions(TimeSpan duration)
    {
        return new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(duration)
            .AddExpirationToken(new CancellationChangeToken(_cacheTokenSource.Token));
    }

    private void CacheSet<T>(string key, T value, TimeSpan? duration = null) =>
        cache.Set(key, value, CreateEntryOptions(duration ?? CacheDuration));

    // Only expose topics that have been assigned a valid ICD-10 category.
    // Topics pending or failing categorisation are not served via the API.
    private static IQueryable<HealthTopic> ICD10ScopedQuery(DbSet<HealthTopic> set) =>
        set.AsNoTracking().Where(c => c.Category != null);

    private static IQueryable<HealthTopic> ApplyTypeFilter(IQueryable<HealthTopic> query, string? topicType)
    {
        return string.IsNullOrWhiteSpace(topicType)
            ? query
            : query.Where(c => c.TopicType != null && EF.Functions.ILike(c.TopicType, topicType));
    }

    private static IQueryable<TopicListItem> ProjectToListItem(IQueryable<HealthTopic> query)
    {
        return query.Select(c => new TopicListItem(
            c.Id,
            c.Name,
            c.Summary,
            c.TopicType,
            c.Category,
            c.LastUpdated));
    }

    private static class CacheKeys
    {
        public static string All(int skip, int take, string? type) =>
            $"topics:all:{skip}:{take}:{type?.ToLowerInvariant()}";

        public static string Count(string? type) =>
            $"topics:count:{type?.ToLowerInvariant()}";

        public static string ByName(string name) =>
            $"topics:name:{name.ToLowerInvariant()}";

        public static string Search(string query, int skip, int take, string? type) =>
            $"topics:search:{query.ToLowerInvariant()}:{skip}:{take}:{type?.ToLowerInvariant()}";

        public static string SearchCount(string query, string? type) =>
            $"topics:searchcount:{query.ToLowerInvariant()}:{type?.ToLowerInvariant()}";

        public const string Types = "topics:types";
    }

    public async Task<IEnumerable<TopicListItem>> GetAllAsync(int skip = 0, int take = 50, string? topicType = null, CancellationToken ct = default)
    {
        var cacheKey = CacheKeys.All(skip, take, topicType);
        if (cache.TryGetValue(cacheKey, out IEnumerable<TopicListItem>? cached) && cached is not null)
            return cached;

        var query = ApplyTypeFilter(ICD10ScopedQuery(context.HealthTopics), topicType);

        var results = await ProjectToListItem(
                query.OrderBy(c => c.Name).Skip(skip).Take(take))
            .ToListAsync(ct);

        CacheSet(cacheKey, results);
        return results;
    }

    public async Task<int> GetCountAsync(string? topicType = null, CancellationToken ct = default)
    {
        var cacheKey = CacheKeys.Count(topicType);
        if (cache.TryGetValue(cacheKey, out int cached))
            return cached;

        var query = ApplyTypeFilter(ICD10ScopedQuery(context.HealthTopics), topicType);

        var count = await query.CountAsync(ct);
        CacheSet(cacheKey, count);
        return count;
    }

    private static readonly object NegativeCacheSentinel = new();

    public async Task<HealthTopic?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        var cacheKey = CacheKeys.ByName(name);
        if (cache.TryGetValue(cacheKey, out object? cached))
            return ReferenceEquals(cached, NegativeCacheSentinel) ? null : cached as HealthTopic;

        var result = await ICD10ScopedQuery(context.HealthTopics)
            .FirstOrDefaultAsync(c => EF.Functions.ILike(c.Name, name), ct);

        // Cache hits for 10 min, misses for 2 min to avoid repeated DB lookups
        var duration = result is not null ? CacheDuration : NegativeCacheDuration;
        CacheSet(cacheKey, (object?)result ?? NegativeCacheSentinel, duration);

        return result;
    }

    private static string EscapeLikeQuery(string query) => query
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");

    public async Task<IEnumerable<TopicListItem>> SearchAsync(string query, int skip = 0, int take = 50, string? topicType = null, CancellationToken ct = default)
    {
        var cacheKey = CacheKeys.Search(query, skip, take, topicType);
        if (cache.TryGetValue(cacheKey, out IEnumerable<TopicListItem>? cached) && cached is not null)
            return cached;

        var escaped = EscapeLikeQuery(query);

        var dbQuery = ApplyTypeFilter(
            ICD10ScopedQuery(context.HealthTopics)
                .Where(c => EF.Functions.ILike(c.Name, $"%{escaped}%", "\\")),
            topicType);

        var results = await ProjectToListItem(
                dbQuery.OrderBy(c => c.Name).Skip(skip).Take(take))
            .ToListAsync(ct);

        CacheSet(cacheKey, results);
        return results;
    }

    public async Task<int> SearchCountAsync(string query, string? topicType = null, CancellationToken ct = default)
    {
        var cacheKey = CacheKeys.SearchCount(query, topicType);
        if (cache.TryGetValue(cacheKey, out int cached))
            return cached;

        var escaped = EscapeLikeQuery(query);

        var dbQuery = ApplyTypeFilter(
            ICD10ScopedQuery(context.HealthTopics)
                .Where(c => EF.Functions.ILike(c.Name, $"%{escaped}%", "\\")),
            topicType);

        var count = await dbQuery.CountAsync(ct);
        CacheSet(cacheKey, count);
        return count;
    }

    public async Task<IReadOnlyList<TopicTypeSummary>> GetDistinctTypesAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKeys.Types, out IReadOnlyList<TopicTypeSummary>? cached) && cached is not null)
            return cached;

        var types = (await ICD10ScopedQuery(context.HealthTopics)
            .Where(c => c.TopicType != null)
            .GroupBy(c => c.TopicType!)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .OrderBy(t => t.Type)
            .ToListAsync(ct))
            .Select(t => new TopicTypeSummary(t.Type, t.Count))
            .ToList();

        CacheSet(CacheKeys.Types, (IReadOnlyList<TopicTypeSummary>)types);
        return types;
    }

    public void InvalidateCache()
    {
        var old = Interlocked.Exchange(ref _cacheTokenSource, new CancellationTokenSource());
        old.Cancel();
        // Do not dispose — outstanding CancellationChangeTokens still reference old.Token.
        // The GC will collect it once all references are released.
    }
}
