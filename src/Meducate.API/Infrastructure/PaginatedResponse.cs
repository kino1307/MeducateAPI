namespace Meducate.API.Infrastructure;

internal sealed record PaginatedResponse<T>(IEnumerable<T> Items, int TotalCount);
