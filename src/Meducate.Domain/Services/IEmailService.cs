namespace Meducate.Domain.Services;

internal sealed record EmailResult(bool Sent, DateTime? RetryAfter = null);

internal interface IEmailService
{
    Task<EmailResult> SendVerificationEmailAsync(string email, string verificationUrl);
    Task<EmailResult> SendLoginEmailAsync(string email, string loginUrl);
    Task<EmailResult> SendRateLimitWarningEmailAsync(string email, string keyName, int currentUsage, int dailyLimit);
}
