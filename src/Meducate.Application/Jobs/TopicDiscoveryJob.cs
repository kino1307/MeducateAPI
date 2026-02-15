using Hangfire;
using Hangfire.Console;
using Hangfire.Server;
using Meducate.Application.Services;
using Microsoft.Extensions.Logging;

namespace Meducate.Application.Jobs;

internal sealed class TopicDiscoveryJob(
    TopicIngestionService ingestionService,
    ILogger<TopicDiscoveryJob> logger)
{
    private readonly TopicIngestionService _ingestionService = ingestionService;
    private readonly ILogger<TopicDiscoveryJob> _logger = logger;

    [DisableConcurrentExecution(timeoutInSeconds: 0)]
    public async Task ExecuteAsync(IJobCancellationToken jobCancellationToken, PerformContext? context = null)
    {
        try
        {
            var ct = jobCancellationToken.ShutdownToken;

            _logger.LogInformation("Starting topic discovery job");
            context?.WriteLine("Starting topic discovery...");

            await _ingestionService.IngestAsync(context, ct);

            _logger.LogInformation("Topic discovery job completed");
            context?.WriteLine("Topic discovery job completed.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Topic discovery job was cancelled (shutdown)");
            context?.WriteLine("Job cancelled (shutdown).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Topic discovery job failed");
            context?.WriteLine($"Job failed: {ex.Message}");
            throw;
        }
    }
}
