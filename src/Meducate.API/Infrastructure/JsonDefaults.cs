using System.Text.Json;

namespace Meducate.API.Infrastructure;

internal static class JsonDefaults
{
    internal static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
