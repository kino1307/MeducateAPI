using System.Threading.RateLimiting;
using Meducate.API.Infrastructure;
using Meducate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Meducate.API.DependencyInjection;

internal static class ApiServiceRegistration
{
    private const long MaxRequestBodySize = 1_048_576; // 1 MB

    internal static IServiceCollection AddMeducateApi(this IServiceCollection services, IConfiguration config, IWebHostEnvironment? env = null)
    {
        // Request size limits
        services.Configure<KestrelServerOptions>(options =>
            options.Limits.MaxRequestBodySize = MaxRequestBodySize);
        services.Configure<FormOptions>(options =>
            options.MultipartBodyLengthLimit = MaxRequestBodySize);

        // JSON serialization — contextual field names based on TopicType
        services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new HealthTopicJsonConverter()));

        // Swagger
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(c => c.OperationFilter<SwaggerHeaderOperationFilter>());

        // Rate limiting
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.GlobalLimiter =
                PartitionedRateLimiter.Create<HttpContext, string>(context =>
                {
                    var apiKey = context.Request.Headers["X-Api-Key"].FirstOrDefault();
                    var keyId = apiKey?.Split('.', 2)[0];

                    var partitionKey = !string.IsNullOrWhiteSpace(keyId)
                        ? $"key:{keyId}"
                        : $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

                    return RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: partitionKey,
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 60,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                            AutoReplenishment = true
                        });
                });
        });

        // CORS
        var allowedOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        var isDevelopment = env?.IsDevelopment() ?? false;

        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                if (allowedOrigins.Length > 0)
                {
                    policy.WithOrigins(allowedOrigins)
                        .AllowCredentials();
                }
                else if (isDevelopment)
                {
                    // In development, allow any localhost origin
                    policy.SetIsOriginAllowed(origin =>
                    {
                        var uri = new Uri(origin);
                        return uri.Host == "localhost" || uri.Host == "127.0.0.1";
                    })
                    .AllowCredentials();
                }
                else
                {
                    throw new InvalidOperationException(
                        "CORS origins are not configured. Set Cors:AllowedOrigins in appsettings or user secrets.");
                }

                policy
                    .WithMethods("GET", "POST", "PATCH", "DELETE", "OPTIONS")
                    .WithHeaders("Content-Type", "Accept", "X-Api-Key", "X-Requested-By", "X-Correlation-Id");
            });
        });

        // Health checks
        services.AddHealthChecks()
            .AddDbContextCheck<MeducateDbContext>("database");
        services.AddHttpContextAccessor();

        return services;
    }
}
