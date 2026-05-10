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
        return app;
    }

    private static IResult Handle(
        string jobName,
        HttpContext http,
        IBackgroundJobClient jobClient,
        IConfiguration config,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Meducate.API.Internal");

        var expectedToken = config["Internal:TriggerToken"];
        if (string.IsNullOrWhiteSpace(expectedToken))
        {
            logger.LogWarning("Internal trigger called but Internal:TriggerToken is not configured");
            return Results.Problem("Internal trigger endpoint is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var provided = http.Request.Headers["X-Internal-Token"].FirstOrDefault() ?? "";
        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        var providedBytes = Encoding.UTF8.GetBytes(provided);

        var tokenValid = providedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);

        if (!tokenValid)
        {
            logger.LogWarning("Internal trigger: rejected request for job '{JobName}' — bad token", jobName);
            return Results.Problem("Unauthorized.", statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!KnownJobs.TryGetValue(jobName, out var enqueue))
        {
            logger.LogWarning("Internal trigger: unknown job '{JobName}'", jobName);
            return Results.Problem($"Unknown job '{jobName}'.", statusCode: StatusCodes.Status404NotFound);
        }

        enqueue(jobClient);
        logger.LogInformation("Internal trigger: enqueued job '{JobName}'", jobName);
        return Results.Accepted(value: new { job = jobName, status = "enqueued" });
    }
}
