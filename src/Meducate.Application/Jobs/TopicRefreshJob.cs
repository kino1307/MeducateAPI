using Hangfire;
using Hangfire.Console;
using Hangfire.Server;
using Meducate.Application.Services;
using Microsoft.Extensions.Logging;

namespace Meducate.Application.Jobs;

internal sealed class TopicRefreshJob(
    TopicRefreshService refreshService,
    ILogger<TopicRefreshJob> logger)
{
    private readonly TopicRefreshService _refreshService = refreshService;
    private readonly ILogger<TopicRefreshJob> _logger = logger;

    [DisableConcurrentExecution(timeoutInSeconds: 0)]
    public async Task ExecuteAsync(IJobCancellationToken jobCancellationToken, PerformContext? context = null)
    {
        try
        {
            var ct = jobCancellationToken.ShutdownToken;
            _logger.LogInformation("Starting topic refresh job");
            context?.WriteLine("Starting topic refresh...");
            await _refreshService.RefreshAllAsync(context, ct);
            _logger.LogInformation("Topic refresh job completed");
            context?.WriteLine("Topic refresh job completed.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Topic refresh job was cancelled (shutdown)");
            context?.WriteLine("Job cancelled (shutdown).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Topic refresh job failed");
            context?.WriteLine($"Job failed: {ex.Message}");
            throw;
        }
    }
}
