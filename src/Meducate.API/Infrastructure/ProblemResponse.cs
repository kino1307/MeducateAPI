using System.Text.Json;

namespace Meducate.API.Infrastructure;

internal static class ProblemResponse
{
    internal static async Task WriteAsync(HttpContext context, int statusCode, string detail)
    {
        if (context.Response.HasStarted) return;

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        var body = new { type = "https://tools.ietf.org/html/rfc7231", title = ReasonPhrase(statusCode), status = statusCode, detail };
        await context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonDefaults.CamelCase));
    }

    private static string ReasonPhrase(int statusCode) => statusCode switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        429 => "Too Many Requests",
        _ => "Internal Server Error"
    };
}
