using Hangfire;
using Hangfire.Console;
using Hangfire.PostgreSql;
using Meducate.Application.Jobs;
using Meducate.Application.Services;
using Meducate.Domain.Repositories;
using Meducate.Domain.Services;
using Meducate.Infrastructure.ApiKeys;
using Meducate.Infrastructure.Auth;
using Meducate.Infrastructure.DataProviders;
using Meducate.Infrastructure.Email;
using Meducate.Infrastructure.LLM;
using Meducate.Infrastructure.Persistence;
using Meducate.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Resend;

namespace Meducate.Infrastructure.DependencyInjection;

internal static class InfrastructureServiceRegistration
{
    internal static IServiceCollection AddMeducateInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        // Database
        var connectionString = config.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Database connection string is not configured. Set ConnectionStrings:DefaultConnection in user secrets or appsettings.");

        if (!connectionString.Contains("GSSEncryptionMode", StringComparison.OrdinalIgnoreCase))
            connectionString += ";GSSEncryptionMode=Disable";

        services.AddDbContext<MeducateDbContext>(options =>
            options.UseNpgsql(connectionString));

        // Repositories
        services.AddScoped<ITopicRepository, TopicRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IOrganisationRepository, OrganisationRepository>();
        services.AddScoped<TopicWriteRepository>();
        services.AddScoped<ITopicWriteRepository>(sp => sp.GetRequiredService<TopicWriteRepository>());
        services.AddScoped<ITopicQueryRepository>(sp => sp.GetRequiredService<TopicWriteRepository>());

        // Email
        var resendToken = config["Resend:ApiToken"];
        if (string.IsNullOrWhiteSpace(resendToken))
            throw new InvalidOperationException("Resend API token is not configured. Set Resend:ApiToken in user secrets or appsettings.");

        services.AddScoped<IEmailService, EmailService>();
        services.AddSingleton<IVerificationLinkBuilder, VerificationLinkBuilder>();
        services.AddOptions();
        services.AddHttpClient<ResendClient>()
            .AddHttpMessageHandler(sp =>
                new ResendRequestLoggingHandler(sp.GetRequiredService<ILogger<ResendRequestLoggingHandler>>()));
        services.Configure<ResendClientOptions>(config.GetSection("Resend"));
        services.AddTransient<IResend, ResendClient>();

        // LLM / Semantic Kernel
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            return SemanticKernelBuilder.CreateKernel(cfg);
        });
        services.AddSingleton<ILLMProcessorLogger, LLMProcessorLogger>();
        services.AddScoped<ILLMProcessor, SemanticKernelLLMProcessor>();

        // Medical data providers
        services.AddHttpClient<MedlinePlusDataProvider>(client =>
        {
            client.BaseAddress = new Uri("https://medlineplus.gov/");
            client.DefaultRequestHeaders.Add("User-Agent", "MeducateAPI/1.0 (medical-education-platform)");
            client.Timeout = TimeSpan.FromSeconds(120);
        });
        services.AddHttpClient<PubMedDataProvider>(client =>
        {
            client.BaseAddress = new Uri("https://eutils.ncbi.nlm.nih.gov/entrez/eutils/");
            client.DefaultRequestHeaders.Add("User-Agent", "MeducateAPI/1.0 (medical-education-platform)");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<IMedicalDataProvider>(sp => sp.GetRequiredService<MedlinePlusDataProvider>());
        services.AddScoped<IMedicalDataProvider>(sp => sp.GetRequiredService<PubMedDataProvider>());

        // Application services
        services.AddScoped<TopicBackfillService>();
        services.AddScoped<TopicIngestionService>();
        services.AddScoped<TopicRefreshService>();
        services.AddScoped<TopicRefreshJob>();
        services.AddScoped<TopicDiscoveryJob>();

        // API keys
        services.AddScoped<ApiKeyService>();
        services.AddScoped<IApiKeyService>(sp => sp.GetRequiredService<ApiKeyService>());
        services.AddScoped<IApiKeyUsageService>(sp => sp.GetRequiredService<ApiKeyService>());

        // Hangfire
        services.AddHangfire(cfg => cfg
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(options =>
                options.UseNpgsqlConnection(connectionString),
                new PostgreSqlStorageOptions
                {
                    InvisibilityTimeout = TimeSpan.FromHours(4)
                })
            .UseConsole()
        );
        services.AddHangfireServer();

        // Infrastructure
        services.AddMemoryCache();

        // Graceful shutdown
        services.Configure<Microsoft.Extensions.Hosting.HostOptions>(options =>
            options.ShutdownTimeout = TimeSpan.FromSeconds(30));

        return services;
    }
}
