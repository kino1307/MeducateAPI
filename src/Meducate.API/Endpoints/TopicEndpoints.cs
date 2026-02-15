using Meducate.API.Infrastructure;
using Meducate.Domain.Entities;
using Meducate.Domain.Repositories;

namespace Meducate.API.Endpoints;

internal static class TopicEndpoints
{
    internal static WebApplication MapTopicEndpoints(this WebApplication app)
    {
        app.MapGet("/api/topics/types", [RequiresApiKey] async (ITopicRepository repo, CancellationToken ct) =>
        {
            var types = await repo.GetDistinctTypesAsync(ct);
            return Results.Ok(types);
        })
        .WithName("ListTopicTypes")
        .WithSummary("List available topic types")
        .WithDescription("Returns a list of distinct topic types currently in the database (e.g. Disease, Drug, Procedure).")
        .Produces<IReadOnlyList<TopicTypeSummary>>()
        .WithTags("Topics");

        app.MapGet("/api/topics/search", [RequiresApiKey] async (string query, int? skip, int? take, string? type, ITopicRepository repo, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(query) || query.Length > ApiConstants.MaxQueryLength)
                return Results.Problem(
                    detail: $"Search query must be between 1 and {ApiConstants.MaxQueryLength} characters.",
                    title: "Bad Request",
                    statusCode: StatusCodes.Status400BadRequest);

            var s = skip is null or < 0 ? 0 : skip.Value;
            var t = take switch
            {
                null or <= 0 => ApiConstants.DefaultPageSize,
                > ApiConstants.MaxPageSize => ApiConstants.MaxPageSize,
                _ => take.Value
            };

            var results = await repo.SearchAsync(query, s, t, type, ct);
            var totalCount = await repo.SearchCountAsync(query, type, ct);
            return Results.Ok(new PaginatedResponse<HealthTopic>(results, totalCount));
        })
        .WithName("SearchTopics")
        .WithSummary("Search topics")
        .WithDescription("Searches health topics by name using a partial match. Use `skip` and `take` to paginate results (default: 50 per page, max: 200). Use `type` to filter by topic type (e.g. Disease, Drug).")
        .Produces<PaginatedResponse<HealthTopic>>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .WithTags("Topics");

        app.MapGet("/api/topics/{name}", [RequiresApiKey] async (string name, ITopicRepository repo, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > ApiConstants.MaxQueryLength)
                return Results.Problem(
                    detail: $"Topic name must be between 1 and {ApiConstants.MaxQueryLength} characters.",
                    title: "Bad Request",
                    statusCode: StatusCodes.Status400BadRequest);

            var topic = await repo.GetByNameAsync(name, ct);

            if (topic is null)
                return Results.Problem(
                    detail: "Topic not found.",
                    title: "Not Found",
                    statusCode: StatusCodes.Status404NotFound);

            return Results.Ok(topic);
        })
        .WithName("GetTopicByName")
        .WithSummary("Get a topic by name")
        .WithDescription("Returns a single health topic matching the given name, including its summary, observations, factors, actions, and citations.")
        .Produces<HealthTopic>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithTags("Topics");

        app.MapGet("/api/topics", [RequiresApiKey] async (int? skip, int? take, string? type, ITopicRepository repo, CancellationToken ct) =>
        {
            var s = skip is null or < 0 ? 0 : skip.Value;

            var t = take switch
            {
                null or <= 0 => ApiConstants.DefaultPageSize,
                > ApiConstants.MaxPageSize => ApiConstants.MaxPageSize,
                _ => take.Value
            };

            var topics = await repo.GetAllAsync(s, t, type, ct);
            var totalCount = await repo.GetCountAsync(type, ct);
            return Results.Ok(new PaginatedResponse<HealthTopic>(topics, totalCount));
        })
        .WithName("ListTopics")
        .WithSummary("List all topics")
        .WithDescription("Returns a paginated list of all health topics. Use `skip` and `take` to paginate (default: 50 per page, max: 200). Use `type` to filter by topic type (e.g. Disease, Drug).")
        .Produces<PaginatedResponse<HealthTopic>>()
        .WithTags("Topics");

        return app;
    }
}
