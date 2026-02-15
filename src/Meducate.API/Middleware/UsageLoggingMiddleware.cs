using System.Net;
using Hangfire;
using Meducate.Domain.Entities;
using Meducate.Domain.Services;

namespace Meducate.API.Middleware;

internal sealed class UsageLoggingMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(HttpContext context, IApiKeyUsageService apiKeyUsageService)
    {
        await _next(context);

        if (context.Items["ApiClient"] is ApiClient client)
        {
            // Use CancellationToken.None — this bookkeeping must complete
            // even if the client disconnects after receiving the response.
            await apiKeyUsageService.LogUsageAsync(
                client.Id,
                context.Request.Path,
                context.Request.Method,
                context.Response.StatusCode,
                client.Organisation?.Name ?? "Unknown",
                TruncateIpAddress(context.Connection.RemoteIpAddress),
                CancellationToken.None);

            // Check if we just crossed the 80% threshold using the count
            // already computed by ApiKeyMiddleware (avoids a redundant DB query)
            if (client.DailyLimit > 0)
            {
                var threshold = (int)(client.DailyLimit * ApiConstants.UsageWarningThreshold);
                var previousCount = context.Items.TryGetValue("ApiUsageCount", out var cached) && cached is int c ? c : 0;
                var usageCount = previousCount + 1; // +1 for the request we just logged

                // Send warning exactly when we hit the threshold
                if (usageCount == threshold)
                {
                    var email = await apiKeyUsageService.GetOrganisationOwnerEmailAsync(client.OrganisationId, CancellationToken.None);

                    if (email is not null)
                    {
                        var keyName = client.Name;
                        var usage = usageCount;
                        var limit = client.DailyLimit;
                        BackgroundJob.Enqueue<IEmailService>(
                            svc => svc.SendRateLimitWarningEmailAsync(email, keyName, usage, limit));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Truncates an IP address for GDPR compliance: zeroes the last octet for IPv4 (/24),
    /// zeroes the last 80 bits for IPv6 (/48). Useful for abuse detection without storing PII.
    /// </summary>
    private static string? TruncateIpAddress(IPAddress? address)
    {
        if (address is null)
            return null;

        var bytes = address.GetAddressBytes();

        if (bytes.Length == 4)
        {
            // IPv4: zero the last octet (x.x.x.0)
            bytes[3] = 0;
        }
        else if (bytes.Length == 16)
        {
            // IPv6: keep first 6 bytes (/48), zero the rest
            for (var i = 6; i < 16; i++)
                bytes[i] = 0;
        }

        return new IPAddress(bytes).ToString();
    }
}
