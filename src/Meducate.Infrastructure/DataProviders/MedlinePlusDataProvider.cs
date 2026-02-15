using System.Text.RegularExpressions;
using System.Xml.Linq;
using Meducate.Application.Helpers;
using Meducate.Domain.Services;
using Microsoft.Extensions.Logging;

namespace Meducate.Infrastructure.DataProviders;

internal sealed partial class MedlinePlusDataProvider(HttpClient httpClient, ILogger<MedlinePlusDataProvider> logger) : IMedicalDataProvider
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly ILogger<MedlinePlusDataProvider> _logger = logger;

    private List<ParsedTopic>? _cachedTopics;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    private sealed record ParsedTopic(string Title, string Summary, List<string> Groups);

    public string SourceName => "MedlinePlus";

    public async Task<RawTopicData?> FetchTopicDataAsync(string topicName, CancellationToken ct = default)
    {
        try
        {
            var topics = await GetOrLoadTopicsAsync(ct);
            var match = topics.FirstOrDefault(t =>
                string.Equals(t.Title, topicName, StringComparison.OrdinalIgnoreCase));

            if (match is null)
                return null;

            return new RawTopicData(topicName, match.Summary, SourceName, match.Groups, ContentHasher.ComputeHash(match.Summary));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MedlinePlus: failed to fetch data for {Topic}", topicName);
            return null;
        }
    }

    public async Task<IReadOnlyList<RawTopicData>> DiscoverTopicsAsync(IReadOnlySet<string> existingNames, CancellationToken ct = default)
    {
        try
        {
            var topics = await GetOrLoadTopicsAsync(ct);

            var newTopics = topics
                .Where(t => !existingNames.Contains(t.Title))
                .Select(t => new RawTopicData(t.Title, t.Summary, SourceName, t.Groups, ContentHasher.ComputeHash(t.Summary)))
                .ToList();

            _logger.LogInformation("MedlinePlus: {Total} health topics, {New} are new",
                topics.Count, newTopics.Count);

            return newTopics;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MedlinePlus: discovery failed");
            return [];
        }
    }

    public async Task<IReadOnlySet<string>> GetKnownTopicNamesAsync(CancellationToken ct = default)
    {
        try
        {
            var topics = await GetOrLoadTopicsAsync(ct);
            return new HashSet<string>(topics.Select(t => t.Title), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MedlinePlus: failed to get known topic names");
            return new HashSet<string>();
        }
    }

    private async Task<List<ParsedTopic>> GetOrLoadTopicsAsync(CancellationToken ct)
    {
        if (_cachedTopics is not null)
            return _cachedTopics;

        await _cacheLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_cachedTopics is not null)
                return _cachedTopics;

            return await LoadTopicsAsync(ct);
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private async Task<List<ParsedTopic>> LoadTopicsAsync(CancellationToken ct)
    {
        var xmlUrl = await ResolveXmlUrlAsync(ct);
        _logger.LogInformation("MedlinePlus: downloading XML from {Url}", xmlUrl);

        var xmlContent = await _httpClient.GetStringAsync(xmlUrl, ct);
        var doc = XDocument.Parse(xmlContent);

        var topics = new List<ParsedTopic>();

        foreach (var topic in doc.Descendants("health-topic"))
        {
            var language = topic.Attribute("language")?.Value;
            if (!string.Equals(language, "English", StringComparison.OrdinalIgnoreCase))
                continue;

            var title = topic.Attribute("title")?.Value?.Trim();
            var summary = topic.Element("full-summary")?.Value?.Trim()
                          ?? topic.Attribute("meta-desc")?.Value?.Trim();

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(summary))
                continue;

            var cleanSummary = StripHtmlTags(summary);
            if (cleanSummary.Length < 50)
                continue;

            var groups = topic.Elements("group")
                .Select(g => g.Value?.Trim() ?? "")
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .ToList();

            topics.Add(new ParsedTopic(title, cleanSummary, groups));
        }

        _logger.LogInformation("MedlinePlus: parsed {Count} health topics from XML", topics.Count);
        _cachedTopics = topics;
        return topics;
    }

    private async Task<string> ResolveXmlUrlAsync(CancellationToken ct)
    {
        var html = await _httpClient.GetStringAsync("xml.html", ct);
        var match = XmlUrlPattern().Match(html);

        if (match.Success)
            return match.Value;

        // Fallback: try today's date directly
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        _logger.LogWarning("MedlinePlus: could not find XML URL on download page, falling back to today's date");
        return $"xml/mplus_topics_{today}.xml";
    }

    private static string StripHtmlTags(string html)
    {
        var text = HtmlTagPattern().Replace(html, "");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = CollapseWhitespace().Replace(text, " ");
        return text.Trim();
    }

    [GeneratedRegex(@"xml/mplus_topics_\d{4}-\d{2}-\d{2}\.xml")]
    private static partial Regex XmlUrlPattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagPattern();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex CollapseWhitespace();
}
