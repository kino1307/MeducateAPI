namespace Meducate.Domain.Services;

internal interface IApiKeyUsageService
{
    Task LogUsageAsync(Guid clientId, string? endpoint, string? method, int statusCode, string? organisationName, string? ipAddress, CancellationToken ct = default);
    Task<string?> GetOrganisationOwnerEmailAsync(Guid organisationId, CancellationToken ct = default);
    Task DeleteUserAccountAsync(Guid userId, CancellationToken ct = default);
}
