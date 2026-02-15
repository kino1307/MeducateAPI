using Meducate.Domain.Entities;
using Meducate.Domain.Services;

namespace Meducate.Application.Helpers;

internal static class TopicHelpers
{
    // Cap per-provider text to avoid blowing LLM token limits
    internal const int MaxCharsPerSource = 15_000;
    internal const int MinSummaryLength = 80;

    internal static string BuildMergedRawSource(IEnumerable<RawTopicData> sources)
    {
        var parts = new List<string>();

        // Prepend group names as context if available
        var groupNames = sources
            .Where(s => s.Groups is not null)
            .SelectMany(s => s.Groups!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (groupNames.Count > 0)
            parts.Add($"[MedlinePlus Groups: {string.Join(", ", groupNames)}]");

        foreach (var s in sources)
        {
            var text = s.RawText;
            if (text.Length > MaxCharsPerSource)
            {
                // Truncate at last space before the limit to avoid cutting mid-word
                var cutoff = text.LastIndexOf(' ', MaxCharsPerSource);
                text = cutoff > 0 ? text[..cutoff] : text[..MaxCharsPerSource];
            }
            parts.Add($"[{s.SourceName}]\n{text}");
        }

        return string.Join("\n---\n", parts);
    }

    internal static string? CheckTopicQuality(HealthTopic topic)
    {
        if (string.IsNullOrWhiteSpace(topic.Summary) || topic.Summary.Length < MinSummaryLength)
            return $"summary too short ({topic.Summary?.Length ?? 0} chars, minimum {MinSummaryLength})";

        // Reject if the summary is just the name restated
        if (topic.Summary.Trim().Equals(topic.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            return "summary just restates the topic name";

        var hasObservations = topic.Observations is { Count: > 0 };
        var hasFactors = topic.Factors is { Count: > 0 };
        var hasActions = topic.Actions is { Count: > 0 };

        if (!hasObservations && !hasFactors && !hasActions)
            return "no observations, factors, or actions populated";

        return null;
    }

    internal static string GetSeenStatus(string topicType) => topicType switch
    {
        "Non-Medical" => "NonMedical",
        "Other" => "Unclassifiable",
        _ => "Accepted"
    };

    internal static string ToTitleCase(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
            words[i] = ProcessWord(words[i]);
        return string.Join(' ', words);

        static string ProcessWord(string word)
        {
            if (word.Length >= 2 && word.All(c => !char.IsLetter(c) || char.IsUpper(c)))
                return word;

            if (word.Contains('(') || word.Contains(')'))
            {
                var result = new System.Text.StringBuilder();
                var currentPart = new System.Text.StringBuilder();

                foreach (var ch in word)
                {
                    if (ch is '(' or ')')
                    {
                        if (currentPart.Length > 0)
                        {
                            result.Append(ProcessSimpleWord(currentPart.ToString()));
                            currentPart.Clear();
                        }
                        result.Append(ch);
                    }
                    else
                    {
                        currentPart.Append(ch);
                    }
                }

                if (currentPart.Length > 0)
                    result.Append(ProcessSimpleWord(currentPart.ToString()));

                return result.ToString();
            }

            if (word.Contains('/'))
            {
                var parts = word.Split('/');
                for (var j = 0; j < parts.Length; j++)
                    parts[j] = ProcessWord(parts[j]);
                return string.Join('/', parts);
            }

            if (word.Contains('-'))
            {
                var parts = word.Split('-');
                for (var j = 0; j < parts.Length; j++)
                    parts[j] = ProcessSimpleWord(parts[j]);
                return string.Join('-', parts);
            }

            return ProcessSimpleWord(word);
        }

        static string ProcessSimpleWord(string word)
        {
            if (string.IsNullOrEmpty(word))
                return word;

            if (word.Length >= 2 && word.All(c => !char.IsLetter(c) || char.IsUpper(c)))
                return word;

            if (word.EndsWith("'s", StringComparison.OrdinalIgnoreCase))
            {
                var basePart = word[..^2];
                if (basePart.Length > 0)
                    return char.ToUpperInvariant(basePart[0]) + basePart[1..].ToLowerInvariant() + "'s";
            }

            return char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
        }
    }
}
