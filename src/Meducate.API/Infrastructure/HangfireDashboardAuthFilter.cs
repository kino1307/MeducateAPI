using Hangfire.Dashboard;

namespace Meducate.API.Infrastructure;

internal sealed class HangfireDashboardAuthFilter : IDashboardAuthorizationFilter
{
    private const string CookieName = "HangfireAuth";
    private const string PasswordKey = "Hangfire:DashboardPassword";

    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        var config = httpContext.RequestServices.GetRequiredService<IConfiguration>();
        var expectedPassword = config[PasswordKey];

        if (string.IsNullOrEmpty(expectedPassword))
            return false;

        // Check cookie first
        if (httpContext.Request.Cookies.TryGetValue(CookieName, out var cookieValue)
            && cookieValue == expectedPassword)
            return true;

        // Check query string
        var queryPassword = httpContext.Request.Query["password"].FirstOrDefault();
        if (!string.IsNullOrEmpty(queryPassword))
        {
            if (queryPassword == expectedPassword)
            {
                httpContext.Response.Cookies.Append(CookieName, expectedPassword, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Strict,
                    MaxAge = TimeSpan.FromHours(12)
                });

                // Redirect to remove password from URL
                var path = httpContext.Request.Path.Value ?? "/hangfire";
                httpContext.Response.Redirect(path);
                return true;
            }

            WriteLoginPage(httpContext, "Invalid password.");
            return false;
        }

        WriteLoginPage(httpContext);
        return false;
    }

    private static void WriteLoginPage(HttpContext httpContext, string? error = null)
    {
        httpContext.Response.ContentType = "text/html";
        httpContext.Response.StatusCode = 401;

        var errorHtml = error is not null
            ? $"<p style=\"color:red;\">{error}</p>"
            : "";

        var html = $"""
            <!DOCTYPE html>
            <html>
            <head><title>Hangfire Dashboard</title></head>
            <body style="font-family:sans-serif;display:flex;justify-content:center;align-items:center;height:100vh;margin:0;">
              <form method="get" style="text-align:center;">
                <h2>Hangfire Dashboard</h2>
                {errorHtml}
                <input type="password" name="password" placeholder="Password" required
                       style="padding:8px;font-size:16px;margin-right:8px;" />
                <button type="submit" style="padding:8px 16px;font-size:16px;">Login</button>
              </form>
            </body>
            </html>
            """;

        httpContext.Response.WriteAsync(html).GetAwaiter().GetResult();
    }
}
