using System.Net;
using Meducate.Domain.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Resend;

namespace Meducate.Infrastructure.Email;

internal sealed class EmailService(IResend resend, IMemoryCache cache, ILogger<EmailService> logger, IConfiguration config) : IEmailService
{
    private readonly IResend _resend = resend;
    private readonly IMemoryCache _cache = cache;
    private readonly ILogger<EmailService> _logger = logger;
    private readonly string _fromAddress = config["Resend:FromAddress"] ?? "no-reply@meducateapi.com";

    private const int MaxRetries = 3;
    private const int MaxEmailsPerRecipient = 3;
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(15);
    private static readonly Lock _rateLimitLock = new();
    private const string ButtonStyle = "display:inline-block;padding:10px 16px;background:#0d6efd;color:#ffffff;text-decoration:none;border-radius:4px;";

    internal static string MaskEmail(string email)
    {
        var atIndex = email.IndexOf('@');
        if (atIndex <= 1) return "***@***";
        return $"{email[0]}***@{email[(atIndex + 1)..]}";
    }

    private static string BuildEmailBody(string bodyHtml)
    {
        return $"""
            <p>Hello,</p>

            {bodyHtml}

            <p>If you didn't request this, you can safely ignore this email.</p>

            <p style="margin-top:24px;font-size:12px;color:#666;">MeducateAPI is intended for educational and informational purposes only.</p>
            """;
    }

    private static string BuildButtonBlock(string url, string label)
    {
        return $"""
            <p>
                <a href="{url}" style="{ButtonStyle}">{label}</a>
            </p>

            <p>If the button doesn't work, copy and paste this link into your browser:</p>

            <p><a href="{url}">{url}</a></p>
            """;
    }

    private async Task SendWithRetryAsync(EmailMessage message, string description)
    {
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                await _resend.EmailSendAsync(message);
                return;
            }
            catch (ResendException ex) when (attempt < MaxRetries && ex.IsTransient)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogWarning(ex, "Attempt {Attempt}/{Max} failed for {Description} (HTTP {StatusCode}), retrying in {Delay}s",
                    attempt, MaxRetries, description, ex.StatusCode, delay.TotalSeconds);
                await Task.Delay(delay);
            }
        }

        // Final attempt — let it throw so callers know it failed
        await _resend.EmailSendAsync(message);
    }

    private sealed record RateState(int Count, DateTime ExpiresAt);

    private async Task<EmailResult> SendAsync(string to, string subject, string bodyHtml, string logDescription)
    {
        var cacheKey = $"emaillimit:{to.ToLowerInvariant()}";

        lock (_rateLimitLock)
        {
            var state = _cache.GetOrCreate(cacheKey, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = RateLimitWindow;
                return new RateState(0, DateTime.UtcNow.Add(RateLimitWindow));
            })!;

            if (state.Count >= MaxEmailsPerRecipient)
            {
                _logger.LogWarning("Email rate limit reached for {Recipient}, suppressing {Description}",
                    MaskEmail(to), logDescription);
                return new EmailResult(false, state.ExpiresAt);
            }

            var remaining = state.ExpiresAt - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                remaining = TimeSpan.FromSeconds(1);
            _cache.Set(cacheKey, state with { Count = state.Count + 1 }, remaining);
        }

        var message = new EmailMessage
        {
            From = _fromAddress,
            To = to,
            Subject = subject,
            HtmlBody = BuildEmailBody(bodyHtml)
        };

        _logger.LogInformation("Sending {Description}", logDescription);
        await SendWithRetryAsync(message, logDescription);
        _logger.LogInformation("Sent {Description}", logDescription);
        return new EmailResult(true);
    }

    public Task<EmailResult> SendVerificationEmailAsync(string email, string verificationUrl)
    {
        var body = $"""
            <p>Thanks for registering with <strong>MeducateAPI</strong>.</p>

            <p>Please verify your email address by clicking the button below:</p>

            {BuildButtonBlock(verificationUrl, "Verify email address")}

            <p>This link will expire in 24 hours.</p>
            """;

        return SendAsync(email, "Verify your MeducateAPI account", body,
            $"verification email to {MaskEmail(email)}");
    }

    public Task<EmailResult> SendLoginEmailAsync(string email, string loginUrl)
    {
        var body = $"""
            <p>We received a request to sign in to your <strong>MeducateAPI</strong> account.</p>

            <p>Click the button below to sign in:</p>

            {BuildButtonBlock(loginUrl, "Sign in to MeducateAPI")}

            <p>This link will expire in 24 hours and can only be used once.</p>
            """;

        return SendAsync(email, "Sign in to MeducateAPI", body,
            $"login email to {MaskEmail(email)}");
    }

    public Task<EmailResult> SendRateLimitWarningEmailAsync(string email, string keyName, int currentUsage, int dailyLimit)
    {
        var percentUsed = (int)((double)currentUsage / dailyLimit * 100);
        var safeKeyName = WebUtility.HtmlEncode(keyName);

        var body = $"""
            <p>Your API key <strong>{safeKeyName}</strong> has used <strong>{percentUsed}%</strong> of its daily request limit.</p>

            <ul>
                <li>Current usage: {currentUsage} requests</li>
                <li>Daily limit: {dailyLimit} requests</li>
            </ul>

            <p>Once the limit is reached, further requests will be rejected until the limit resets at midnight UTC.</p>

            <p>If you need a higher limit, please contact us.</p>
            """;

        return SendAsync(email, $"MeducateAPI: {percentUsed}% of daily limit used", body,
            $"rate limit warning to {MaskEmail(email)}");
    }

    internal static string FormatRetryAfter(DateTime retryAfter)
    {
        var remaining = retryAfter - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero) return "now";

        return remaining.TotalMinutes >= 1
            ? $"{(int)Math.Ceiling(remaining.TotalMinutes)} minute(s)"
            : $"{(int)Math.Ceiling(remaining.TotalSeconds)} second(s)";
    }
}
