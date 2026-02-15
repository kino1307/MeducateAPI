using Meducate.Application.DTOs;
using Meducate.Domain.Repositories;
using Meducate.Domain.Services;
using Microsoft.AspNetCore.Authorization;
using static Meducate.API.Endpoints.EndpointHelpers;

namespace Meducate.API.Endpoints;

internal static class OrgEndpoints
{
    internal static WebApplication MapOrgEndpoints(this WebApplication app)
    {
        app.MapPost("/api/orgs", [Authorize] async (CreateOrganisationRequest request, IUserRepository userRepo, IOrganisationRepository orgRepo, HttpContext http, CancellationToken ct) =>
        {
            var (user, error) = await GetVerifiedUserAsync(http, userRepo, ct);
            if (error is not null) return error;

            var existing = await orgRepo.GetByUserIdAsync(user!.Id, ct);
            if (existing != null)
                return Results.Problem(
                    detail: "An organisation already exists for this account.",
                    statusCode: StatusCodes.Status400BadRequest);

            var org = await orgRepo.CreateAsync(request.OrganisationName, user.Id, ct);

            return Results.Created($"/api/orgs/{org.Id}", org.Id);
        });

        app.MapPost("/api/orgs/{id:guid}/keys", [Authorize] async (Guid id, CreateApiKeyRequest? request, IOrganisationRepository orgRepo, IUserRepository userRepo, IApiKeyService keys, HttpContext http, CancellationToken ct) =>
        {
            var (user, userError) = await GetVerifiedUserAsync(http, userRepo, ct);
            if (userError is not null) return userError;

            var (org, orgError) = await GetOwnedOrgAsync(id, user!, orgRepo, ct);
            if (orgError is not null) return orgError;

            if (request?.Name is not null && (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > ApiConstants.MaxKeyNameLength))
                return Results.Problem(
                    detail: $"Key name must be between 1 and {ApiConstants.MaxKeyNameLength} characters.",
                    statusCode: StatusCodes.Status400BadRequest);

            var activeKeyCount = await keys.GetActiveKeyCountAsync(id, ct);
            if (activeKeyCount >= ApiConstants.MaxKeysPerOrg)
            {
                return Results.Problem(
                    detail: $"Maximum of {ApiConstants.MaxKeysPerOrg} active API keys per organisation.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await keys.CreateKeyForOrganisationAsync(id, request?.Name, ct: ct);

            if (result is null)
            {
                return Results.Problem(
                    detail: "Organisation not found.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            var (client, rawKey) = result.Value;

            return Results.Created(
                $"/api/orgs/{id}/keys/{client.KeyId}",
                new ApiKeyResult(
                    client.Id,
                    client.KeyId,
                    client.OrganisationId,
                    rawKey));
        });

        app.MapGet("/api/orgs/{id:guid}/keys", [Authorize] async (Guid id, IOrganisationRepository orgRepo, IUserRepository userRepo, IApiKeyService keys, HttpContext http, CancellationToken ct) =>
        {
            var (user, userError) = await GetVerifiedUserAsync(http, userRepo, ct);
            if (userError is not null) return userError;

            var (org, orgError) = await GetOwnedOrgAsync(id, user!, orgRepo, ct);
            if (orgError is not null) return orgError;

            var activeKeys = await keys.GetActiveKeysForOrgAsync(id, ct);
            var usageCounts = await keys.GetTodayUsageCountsAsync([.. activeKeys.Select(k => k.Id)], ct);

            var result = activeKeys.Select(k => new
            {
                k.Id,
                k.KeyId,
                k.Name,
                k.CreatedAt,
                k.LastUsedAt,
                k.ExpiresAt,
                k.DailyLimit,
                UsageToday = usageCounts.GetValueOrDefault(k.Id, 0)
            });

            return Results.Ok(result);
        });

        app.MapPatch("/api/orgs/{orgId:guid}/keys/{keyId:guid}", [Authorize] async (Guid orgId, Guid keyId, UpdateApiKeyRequest request, IOrganisationRepository orgRepo, IUserRepository userRepo, IApiKeyService keys, HttpContext http, CancellationToken ct) =>
        {
            var (user, userError) = await GetVerifiedUserAsync(http, userRepo, ct);
            if (userError is not null) return userError;

            var (org, orgError) = await GetOwnedOrgAsync(orgId, user!, orgRepo, ct);
            if (orgError is not null) return orgError;

            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > ApiConstants.MaxKeyNameLength)
                return Results.Problem(
                    detail: $"Key name must be between 1 and {ApiConstants.MaxKeyNameLength} characters.",
                    statusCode: StatusCodes.Status400BadRequest);

            var keyToRename = await keys.GetActiveKeyForOrgAsync(keyId, orgId, ct);

            if (keyToRename is null)
                return Results.Problem(
                    detail: "API key not found.",
                    statusCode: StatusCodes.Status404NotFound);

            await keys.RenameKeyAsync(keyId, request.Name, ct);

            return Results.NoContent();
        });

        app.MapDelete("/api/orgs/{orgId:guid}/keys/{keyId:guid}", [Authorize] async (Guid orgId, Guid keyId, IOrganisationRepository orgRepo, IUserRepository userRepo, IApiKeyService keys, HttpContext http, CancellationToken ct) =>
        {
            var (user, userError) = await GetVerifiedUserAsync(http, userRepo, ct);
            if (userError is not null) return userError;

            var (org, orgError) = await GetOwnedOrgAsync(orgId, user!, orgRepo, ct);
            if (orgError is not null) return orgError;

            var keyToRevoke = await keys.GetActiveKeyForOrgAsync(keyId, orgId, ct);

            if (keyToRevoke is null)
            {
                return Results.Problem(
                    detail: "API key not found.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            await keys.DisableKeyAsync(keyId, ct);

            return Results.NoContent();
        });

        return app;
    }
}
