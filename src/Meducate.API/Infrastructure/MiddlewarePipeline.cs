using Hangfire;
using Meducate.API.Endpoints;
using Meducate.API.Middleware;
using Meducate.Application.Jobs;
using Microsoft.AspNetCore.HttpOverrides;

namespace Meducate.API.Infrastructure;

internal static class MiddlewarePipeline
{
    internal static WebApplication UseMeducatePipeline(this WebApplication app)
    {
        var forwardedHeadersOptions = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        };
        forwardedHeadersOptions.KnownIPNetworks.Clear();
        forwardedHeadersOptions.KnownProxies.Clear();
        app.UseForwardedHeaders(forwardedHeadersOptions);

        if (!app.Environment.IsDevelopment())
        {
            app.UseHttpsRedirection();
            app.UseHsts();
        }

        app.UseRouting();
        app.UseCors();

        // Outermost middleware — wraps everything so all responses get headers and exceptions are caught
        app.UseMiddleware<GlobalExceptionMiddleware>();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<RequestTimingMiddleware>();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        app.UseMiddleware<CsrfProtectionMiddleware>();
        app.UseMiddleware<ApiKeyMiddleware>();
        app.UseMiddleware<UsageLoggingMiddleware>();

        app.UseSwagger();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwaggerUI();
        }

        app.UseHangfireDashboard("/hangfire", new DashboardOptions
        {
            Authorization = [new HangfireDashboardAuthFilter()]
        });

        var jobManager = app.Services.GetRequiredService<IRecurringJobManager>();

        jobManager.RemoveIfExists("upsert-medical-conditions");

        jobManager.AddOrUpdate<TopicDiscoveryJob>(
            "discover-medical-conditions",
            job => job.ExecuteAsync(JobCancellationToken.Null),
            "0 2 * * *"); // 2 AM UTC

        jobManager.AddOrUpdate<TopicRefreshJob>(
            "refresh-medical-conditions",
            job => job.ExecuteAsync(JobCancellationToken.Null),
            "0 3 * * *"); // 3 AM UTC

        if (!app.Environment.IsDevelopment())
        {
            var jobClient = app.Services.GetRequiredService<IBackgroundJobClient>();
            jobClient.Enqueue<TopicRefreshJob>(job => job.ExecuteAsync(JobCancellationToken.Null));
        }

        app.MapHealthChecks("/health");
        app.MapAuthEndpoints();
        app.MapOrgEndpoints();
        app.MapTopicEndpoints();

        return app;
    }
}
