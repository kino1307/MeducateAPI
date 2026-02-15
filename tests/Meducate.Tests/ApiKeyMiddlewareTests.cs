using Meducate.API.Infrastructure;
using Meducate.API.Middleware;
using Meducate.Domain.Entities;
using Meducate.Domain.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meducate.Tests;

public class ApiKeyMiddlewareTests
{
    private static ApiKeyMiddleware CreateMiddleware(RequestDelegate? next = null)
    {
        next ??= _ => Task.CompletedTask;
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new ApiKeyMiddleware(next, cache, NullLogger<ApiKeyMiddleware>.Instance);
    }

    private static DefaultHttpContext CreateContext(bool requiresKey = true)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        if (requiresKey)
        {
            var endpoint = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new RequiresApiKeyAttribute()), "test");
            context.SetEndpoint(endpoint);
        }

        return context;
    }

    [Fact]
    public async Task NoRequiresApiKeyAttribute_PassesThrough()
    {
        var called = false;
        var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });
        var context = CreateContext(requiresKey: false);

        await middleware.InvokeAsync(context, new StubApiKeyService());

        Assert.True(called);
    }

    [Fact]
    public async Task MissingHeader_Returns401()
    {
        var middleware = CreateMiddleware();
        var context = CreateContext();

        await middleware.InvokeAsync(context, new StubApiKeyService());

        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task MalformedKey_NoDot_Returns401()
    {
        var middleware = CreateMiddleware();
        var context = CreateContext();
        context.Request.Headers["X-Api-Key"] = "nodothere";

        await middleware.InvokeAsync(context, new StubApiKeyService());

        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task UnknownKeyId_Returns401()
    {
        var middleware = CreateMiddleware();
        var context = CreateContext();
        context.Request.Headers["X-Api-Key"] = "unknown.secret";

        await middleware.InvokeAsync(context, new StubApiKeyService());

        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task InactiveKey_Returns401()
    {
        var service = new StubApiKeyService
        {
            Client = new ApiClient { KeyId = "key1", IsActive = false }
        };
        var middleware = CreateMiddleware();
        var context = CreateContext();
        context.Request.Headers["X-Api-Key"] = "key1.secret";

        await middleware.InvokeAsync(context, service);

        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task ExpiredKey_Returns401()
    {
        var service = new StubApiKeyService
        {
            Client = new ApiClient { KeyId = "key1", IsActive = true, ExpiresAt = DateTime.UtcNow.AddDays(-1) }
        };
        var middleware = CreateMiddleware();
        var context = CreateContext();
        context.Request.Headers["X-Api-Key"] = "key1.secret";

        await middleware.InvokeAsync(context, service);

        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvalidSecret_Returns401()
    {
        var service = new StubApiKeyService
        {
            Client = new ApiClient { KeyId = "key1", IsActive = true },
            SecretValid = false
        };
        var middleware = CreateMiddleware();
        var context = CreateContext();
        context.Request.Headers["X-Api-Key"] = "key1.wrongsecret";

        await middleware.InvokeAsync(context, service);

        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task RateLimitExceeded_Returns429()
    {
        var client = new ApiClient { KeyId = "key1", IsActive = true, DailyLimit = 10 };
        var service = new StubApiKeyService
        {
            Client = client,
            SecretValid = true,
            UsageCount = 10
        };
        var middleware = CreateMiddleware();
        var context = CreateContext();
        context.Request.Headers["X-Api-Key"] = "key1.validsecret";

        await middleware.InvokeAsync(context, service);

        Assert.Equal(429, context.Response.StatusCode);
        Assert.Equal("86400", context.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task ValidKey_PassesThrough_AndSetsContextItems()
    {
        var called = false;
        var client = new ApiClient { KeyId = "key1", IsActive = true, DailyLimit = 100 };
        var service = new StubApiKeyService
        {
            Client = client,
            SecretValid = true,
            UsageCount = 5
        };
        var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });
        var context = CreateContext();
        context.Request.Headers["X-Api-Key"] = "key1.validsecret";

        await middleware.InvokeAsync(context, service);

        Assert.True(called);
        Assert.Same(client, context.Items["ApiClient"]);
        Assert.Equal(5, context.Items["ApiUsageCount"]);
    }

    [Fact]
    public async Task UnlimitedKey_PassesThrough_RegardlessOfUsage()
    {
        var called = false;
        var client = new ApiClient { KeyId = "key1", IsActive = true, DailyLimit = 0 };
        var service = new StubApiKeyService
        {
            Client = client,
            SecretValid = true,
            UsageCount = 999999
        };
        var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });
        var context = CreateContext();
        context.Request.Headers["X-Api-Key"] = "key1.validsecret";

        await middleware.InvokeAsync(context, service);

        Assert.True(called);
    }

    private sealed class StubApiKeyService : IApiKeyService
    {
        public ApiClient? Client { get; set; }
        public bool SecretValid { get; set; }
        public int UsageCount { get; set; }

        public Task<ApiClient?> GetByKeyIdAsync(string keyId, CancellationToken ct = default)
            => Task.FromResult(Client);

        public Task<bool> ValidateSecretAsync(ApiClient client, string secret)
            => Task.FromResult(SecretValid);

        public Task<int> GetTodayUsageCountAsync(Guid clientId, CancellationToken ct = default)
            => Task.FromResult(UsageCount);

        public Task<(ApiClient client, string rawKey)?> CreateKeyForOrganisationAsync(Guid organisationId, string? name = null, int dailyLimit = 1000, CancellationToken ct = default)
            => Task.FromResult<(ApiClient, string)?>(null);

        public Task DisableKeyAsync(Guid clientId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<int> GetActiveKeyCountAsync(Guid organisationId, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<List<ApiClient>> GetActiveKeysForOrgAsync(Guid organisationId, CancellationToken ct = default)
            => Task.FromResult(new List<ApiClient>());

        public Task<ApiClient?> GetActiveKeyForOrgAsync(Guid keyId, Guid organisationId, CancellationToken ct = default)
            => Task.FromResult<ApiClient?>(null);

        public Task<Dictionary<Guid, int>> GetTodayUsageCountsAsync(List<Guid> clientIds, CancellationToken ct = default)
            => Task.FromResult(new Dictionary<Guid, int>());

        public Task<bool> RenameKeyAsync(Guid clientId, string name, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> HasActiveKeysAsync(Guid organisationId, CancellationToken ct = default)
            => Task.FromResult(false);
    }
}
