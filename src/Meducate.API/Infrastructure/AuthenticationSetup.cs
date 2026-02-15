using System.Security.Claims;
using System.Security.Cryptography;
using Meducate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace Meducate.API.Infrastructure;

internal static class AuthenticationSetup
{
    internal static IServiceCollection AddMeducateAuth(this IServiceCollection services)
    {
        services.AddAuthentication("MeducateAPIAuth")
            .AddCookie("MeducateAPIAuth", o =>
            {
                o.Cookie.Name = "meducateapi_auth";
                o.Cookie.Path = "/";
                o.Cookie.HttpOnly = true;
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.Cookie.SameSite = SameSiteMode.Strict;

                o.LoginPath = "/register";
                o.AccessDeniedPath = "/register";

                o.ExpireTimeSpan = TimeSpan.FromHours(8);
                o.SlidingExpiration = true;

                o.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = ctx =>
                    {
                        if (ctx.Request.Path.StartsWithSegments("/api"))
                        {
                            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return Task.CompletedTask;
                        }

                        ctx.Response.Redirect(ctx.RedirectUri);
                        return Task.CompletedTask;
                    },

                    OnRedirectToAccessDenied = ctx =>
                    {
                        if (ctx.Request.Path.StartsWithSegments("/api"))
                        {
                            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return Task.CompletedTask;
                        }

                        ctx.Response.Redirect(ctx.RedirectUri);
                        return Task.CompletedTask;
                    },

                    OnValidatePrincipal = async ctx =>
                    {
                        var authTimeClaim = ctx.Principal?.FindFirstValue("auth_time");
                        if (authTimeClaim is null
                            || !long.TryParse(authTimeClaim, out var authTimeUnix)
                            || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - authTimeUnix > ApiConstants.MaxAuthAgeSeconds)
                        {
                            ctx.RejectPrincipal();
                            await ctx.HttpContext.SignOutAsync("MeducateAPIAuth");
                            return;
                        }

                        var stampClaim = ctx.Principal?.FindFirstValue("security_stamp");
                        if (stampClaim is null)
                        {
                            ctx.RejectPrincipal();
                            await ctx.HttpContext.SignOutAsync("MeducateAPIAuth");
                            return;
                        }

                        var lastCheck = ctx.Properties.Items.TryGetValue("last_stamp_check", out var ts)
                            && long.TryParse(ts, out var lastUnix)
                            ? lastUnix
                            : 0L;

                        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lastCheck < ApiConstants.StampCheckIntervalSeconds)
                            return;

                        var userId = ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                        if (userId is null || !Guid.TryParse(userId, out var uid))
                        {
                            ctx.RejectPrincipal();
                            await ctx.HttpContext.SignOutAsync("MeducateAPIAuth");
                            return;
                        }

                        var db = ctx.HttpContext.RequestServices.GetRequiredService<MeducateDbContext>();
                        var stamp = await db.Users
                            .Where(u => u.Id == uid)
                            .Select(u => new { u.SecurityStamp })
                            .FirstOrDefaultAsync(ctx.HttpContext.RequestAborted);

                        if (stamp is null || !CryptographicOperations.FixedTimeEquals(
                            System.Text.Encoding.UTF8.GetBytes(stamp.SecurityStamp),
                            System.Text.Encoding.UTF8.GetBytes(stampClaim)))
                        {
                            ctx.RejectPrincipal();
                            await ctx.HttpContext.SignOutAsync("MeducateAPIAuth");
                            return;
                        }

                        ctx.Properties.Items["last_stamp_check"] =
                            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                        ctx.ShouldRenew = true;
                    }
                };
            });

        services.AddAuthorization();
        services.AddValidation();

        return services;
    }
}
