using Meducate.API.Middleware;
using Meducate.Domain.Entities;
using Meducate.Domain.Services;
using Microsoft.AspNetCore.Http;

namespace Meducate.Tests;

public class UsageLoggingMiddlewareTests
{
    [Fact]
    public async Task CallsNextMiddleware()
    {
        var called = false;
        var middleware = new UsageLoggingMiddleware(_ => { called = true; return Task.CompletedTask; });
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context, new StubUsageService());

        Assert.True(called);
    }

    [Fact]
    public async Task LogsUsage_WhenApiClientInItems()
    {
        var service = new StubUsageService();
        var middleware = new UsageLoggingMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/topics";
        var client = new ApiClient { Id = Guid.NewGuid(), IsActive = true, DailyLimit = 0 };
        client.Organisation = new Organisation { Name = "TestOrg" };
        context.Items["ApiClient"] = client;
        context.Items["ApiUsageCount"] = 5;

        await middleware.InvokeAsync(context, service);

        Assert.True(service.LogUsageCalled);
        Assert.Equal(client.Id, service.LoggedClientId);
    }

    [Fact]
    public async Task SkipsLogging_WhenNoApiClientInItems()
    {
        var service = new StubUsageService();
        var middleware = new UsageLoggingMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context, service);

        Assert.False(service.LogUsageCalled);
    }

    private sealed class StubUsageService : IApiKeyUsageService
    {
        public bool LogUsageCalled { get; private set; }
        public Guid LoggedClientId { get; private set; }

        public Task LogUsageAsync(Guid clientId, string? endpoint, string? method, int statusCode, string? organisationName, string? queryString, CancellationToken ct = default)
        {
            LogUsageCalled = true;
            LoggedClientId = clientId;
            return Task.CompletedTask;
        }

        public Task<string?> GetOrganisationOwnerEmailAsync(Guid organisationId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task DeleteUserAccountAsync(Guid userId, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
