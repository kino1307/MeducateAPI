using System.Security.Claims;
using Meducate.Application.DTOs;
using Meducate.Domain.Enums;
using Meducate.Domain.Repositories;
using Meducate.Domain.Services;
using Meducate.Infrastructure.Email;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

namespace Meducate.API.Endpoints;

internal static class AuthEndpoints
{
    internal static WebApplication MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/logout", [Authorize] async (HttpContext http) =>
        {
            await http.SignOutAsync("MeducateAPIAuth");
            return Results.NoContent();
        });

        app.MapPost("/api/users/register", [AllowAnonymous] async (RegisterRequest request, IUserRepository users, IEmailService emailSvc, IVerificationLinkBuilder linkBuilder, ILogger<Program> logger, CancellationToken ct) =>
        {
            var user = await users.GetOrCreateAsync(request.Email, ct);

            EmailResult emailResult;
            try
            {
                if (!user.IsEmailVerified)
                {
                    var verifyUrl = linkBuilder.Build(user.VerificationToken!);
                    emailResult = await emailSvc.SendVerificationEmailAsync(user.Email, verifyUrl);
                }
                else
                {
                    user.RotateVerificationToken();
                    await users.SaveChangesAsync(ct);

                    var loginUrl = linkBuilder.Build(user.VerificationToken!);
                    emailResult = await emailSvc.SendLoginEmailAsync(user.Email, loginUrl);
                }
            }
            catch (Resend.ResendException ex)
            {
                logger.LogError(ex, "Resend email failed (HTTP {StatusCode}, {ErrorType}): {ErrorMessage}",
                    ex.StatusCode, ex.ErrorType, ex.Message);
                return Results.Problem(
                    detail: "Email delivery failed. Please try again later.",
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send email for registration");
                return Results.Problem(
                    detail: "Something went wrong sending your email. Please try again.",
                    statusCode: StatusCodes.Status502BadGateway);
            }

            if (!emailResult.Sent && emailResult.RetryAfter is not null)
            {
                return Results.Problem(
                    detail: $"Too many email requests. Please try again in {EmailService.FormatRetryAfter(emailResult.RetryAfter.Value)}.",
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            return Results.Ok(new { message = "If an account exists for this email, we\u2019ve sent a verification link." });
        });

        app.MapPost("/api/users/verify", [AllowAnonymous] async (VerifyUserRequest request, IUserRepository users, IEmailService emailSvc, IVerificationLinkBuilder urlBuilder, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Token))
            {
                return Results.Problem(
                    detail: "This verification link is invalid.",
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["verifyResult"] = VerifyUserResult.Invalid });
            }

            var user = await users.GetByVerificationTokenAsync(request.Token, ct);

            if (user is not null)
            {
                if (user.IsEmailVerified)
                {
                    await Services.AuthSignIn.SignInAsync(http, user);

                    user.VerificationToken = null;
                    user.VerificationTokenExpiresAt = null;
                    await users.SaveChangesAsync(ct);

                    return Results.Ok(new { result = "AlreadyVerified", message = "Your email is already verified." });
                }

                await users.VerifyAsync(user, ct);

                await Services.AuthSignIn.SignInAsync(http, user);

                return Results.Ok(new { result = "Success", message = "Your email has been verified." });
            }

            var expiredUser = await users.GetByVerificationTokenIncludingExpiredAsync(request.Token, ct);

            if (expiredUser is not null && !expiredUser.IsEmailVerified)
            {
                expiredUser.RotateVerificationToken();
                await users.SaveChangesAsync(ct);

                var verifyUrl = urlBuilder.Build(expiredUser.VerificationToken!);
                var resendResult = await emailSvc.SendVerificationEmailAsync(expiredUser.Email, verifyUrl);

                if (!resendResult.Sent && resendResult.RetryAfter is not null)
                {
                    return Results.Problem(
                        detail: $"This verification link has expired. Please try again in {EmailService.FormatRetryAfter(resendResult.RetryAfter.Value)}.",
                        statusCode: StatusCodes.Status429TooManyRequests,
                        extensions: new Dictionary<string, object?> { ["verifyResult"] = VerifyUserResult.Expired });
                }

                return Results.Problem(
                    detail: "This verification link has expired. We\u2019ve sent you a new one.",
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["verifyResult"] = VerifyUserResult.Expired });
            }

            return Results.Problem(
                detail: "This verification link is invalid.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["verifyResult"] = VerifyUserResult.Invalid });
        });

        app.MapGet("/api/users/me", [Authorize] async (IUserRepository users, IOrganisationRepository orgs, IApiKeyService apiKeys, HttpContext http, CancellationToken ct) =>
        {
            var rawId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (rawId is null || !Guid.TryParse(rawId, out var userId))
            {
                return Results.Problem(
                    detail: "Invalid session.",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var user = await users.GetByIdAsync(userId, ct);

            if (user is null)
            {
                return Results.Problem(
                    detail: "User not found.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            var org = await orgs.GetByUserIdAsync(user.Id, ct);

            var hasApiKeys = org is not null
                && await apiKeys.HasActiveKeysAsync(org.Id, ct);

            return Results.Ok(new { user.Email, user.IsEmailVerified, OrganisationId = org?.Id, OrganisationName = org?.Name, HasApiKeys = hasApiKeys });
        });

        app.MapDelete("/api/users/me", [Authorize] async (IUserRepository users, IApiKeyUsageService apiKeys, HttpContext http, CancellationToken ct) =>
        {
            // Require a fresh session (signed in within last 10 minutes) for destructive actions
            var authTimeClaim = http.User.FindFirstValue("auth_time");
            if (authTimeClaim is null
                || !long.TryParse(authTimeClaim, out var authTimeUnix)
                || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - authTimeUnix > ApiConstants.FreshAuthWindowSeconds)
            {
                return Results.Problem(
                    detail: "Please sign in again before deleting your account.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var rawId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (rawId is null || !Guid.TryParse(rawId, out var userId))
            {
                return Results.Problem(
                    detail: "Invalid session.",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var user = await users.GetByIdAsync(userId, ct);

            if (user is null)
            {
                return Results.Problem(
                    detail: "User not found.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            await apiKeys.DeleteUserAccountAsync(userId, ct);

            // Sign out
            await http.SignOutAsync("MeducateAPIAuth");

            return Results.NoContent();
        });

        return app;
    }
}
