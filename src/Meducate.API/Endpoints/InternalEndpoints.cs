using System.Security.Cryptography;
using System.Text;
using Hangfire;
using Meducate.Application.Jobs;

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
}
