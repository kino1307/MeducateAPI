using Meducate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Meducate.Infrastructure.DependencyInjection;

public static class MigrationExtensions
{
    public static async Task ApplyMigrationsAsync(this Microsoft.AspNetCore.Builder.WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeducateDbContext>();
        await db.Database.MigrateAsync();
    }
}
