using System.Security.Cryptography;
using System.Text;
using Hangfire;
using Meducate.Application.Jobs;
using Meducate.Domain.Repositories;

namespace Meducate.API.Endpoints;

internal static class InternalEndpoints
{
    private static readonly Dictionary<string, Action<IBackgroundJobClient>> KnownJobs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["data-integrity-check"]        = c => c.Enqueue<DataIntegrityCheckJob>(j => j.ExecuteAsync(JobCancellationToken.Null, null)),
        ["refresh-medical-conditions"]  = c => c.Enqueue<TopicRefreshJob>(j => j.ExecuteAsync(JobCancellationToken.Null, null)),
        ["discover-medical-conditions"] = c => c.Enqueue<TopicDiscoveryJob>(j => j.ExecuteAsync(JobCancellationToken.Null, null)),
    };

    internal static WebApplication MapInternalEndpoints(this WebApplication app)
    {
        app.MapPost("/internal/jobs/{jobName}", Handle);
        app.MapGet("/internal/jobs/{jobName}/last-run", HandleLastRun);
        app.MapGet("/internal/topics/sample", HandleTopicSample);
        return app;
    }

    private static IResult? CheckToken(HttpContext http, IConfiguration config, ILogger logger, string context)
    {
        var expectedToken = config["Internal:TriggerToken"];
        if (string.IsNullOrWhiteSpace(expectedToken))
        {
            logger.LogWarning("Internal endpoint called but Internal:TriggerToken is not configured");
            return Results.Problem("Internal endpoint is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var provided = http.Request.Headers["X-Internal-Token"].FirstOrDefault() ?? "";
        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        var providedBytes = Encoding.UTF8.GetBytes(provided);

        var tokenValid = providedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);

        if (!tokenValid)
        {
            logger.LogWarning("Internal endpoint: rejected request ({Context}) — bad token", context);
            return Results.Problem("Unauthorized.", statusCode: StatusCodes.Status401Unauthorized);
        }

        return null; // authorized
    }

    private static IResult Handle(
        string jobName,
        HttpContext http,
        IBackgroundJobClient jobClient,
        IConfiguration config,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Meducate.API.Internal");

        var authError = CheckToken(http, config, logger, $"trigger {jobName}");
        if (authError is not null) return authError;

        if (!KnownJobs.TryGetValue(jobName, out var enqueue))
        {
            logger.LogWarning("Internal trigger: unknown job '{JobName}'", jobName);
            return Results.Problem($"Unknown job '{jobName}'.", statusCode: StatusCodes.Status404NotFound);
        }

        enqueue(jobClient);
        logger.LogInformation("Internal trigger: enqueued job '{JobName}'", jobName);
        return Results.Accepted(value: new { job = jobName, status = "enqueued" });
    }

    private static IResult HandleLastRun(
        string jobName,
        HttpContext http,
        JobResultStore resultStore,
        IConfiguration config,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Meducate.API.Internal");

        var authError = CheckToken(http, config, logger, $"last-run {jobName}");
        if (authError is not null) return authError;

        var result = resultStore.Get(jobName);
        if (result is null)
            return Results.Problem(
                $"No result available for '{jobName}'. Either the job has not run since the last deployment, or the job name is unknown.",
                statusCode: StatusCodes.Status404NotFound);

        return Results.Ok(new
        {
            job          = jobName,
            ranAt        = result.RanAt,
            durationMs   = result.DurationMs,
            batchIndex   = result.BatchIndex,
            totalBatches = result.TotalBatches,
            topicsChecked = result.TopicsChecked,
            failures     = result.Failures,
            warnings     = result.Warnings,
            failureDetails = result.FailureDetails,
        });
    }

    private static async Task<IResult> HandleTopicSample(
        HttpContext http,
        ITopicQueryRepository queryRepo,
        IConfiguration config,
        ILoggerFactory loggerFactory,
        int count = 10,
        CancellationToken ct = default)
    {
        var logger = loggerFactory.CreateLogger("Meducate.API.Internal");

        var authError = CheckToken(http, config, logger, "topics/sample");
        if (authError is not null) return authError;

        count = Math.Clamp(count, 1, 50);

        var total = await queryRepo.GetServedTopicCountAsync(ct);
        if (total == 0)
            return Results.Problem("No served topics found.", statusCode: StatusCodes.Status404NotFound);

        var maxSkip = Math.Max(0, total - count);
        var skip = Random.Shared.Next(0, maxSkip + 1);
        var topics = await queryRepo.GetServedTopicBatchAsync(skip, count, ct);

        var result = topics.Select(t => new
        {
            t.Name,
            t.Category,
            t.TopicType,
            t.Summary,
            t.LastUpdated,
            hasObservations = t.Observations?.Count > 0,
            hasFactor       = t.Factors?.Count > 0,
            hasActions      = t.Actions?.Count > 0,
            citationCount   = t.Citations?.Count ?? 0,
            rawSourceLength = t.RawSource?.Length ?? 0,
            rawSourcePreview = t.RawSource is { Length: > 0 }
                ? t.RawSource[..Math.Min(300, t.RawSource.Length)]
                : null,
        });

        return Results.Ok(new { total, skip, count = topics.Count, topics = result });
    }
}
