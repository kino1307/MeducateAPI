using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Meducate.API.Infrastructure;

internal sealed class SwaggerHeaderOperationFilter : IOperationFilter
{
    private const string HeaderName = "X-Api-Key";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (operation == null) return;

        var requiresKey = context.ApiDescription?
            .ActionDescriptor?
            .EndpointMetadata?
            .OfType<RequiresApiKeyAttribute>()
            .Any() == true;

        if (!requiresKey)
            return;

        operation.Parameters ??= [];

        if (operation.Parameters.Any(p => p.Name == HeaderName))
            return;

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = HeaderName,
            In = ParameterLocation.Header,
            Required = true,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            Description = "API key in the format keyId.secret"
        });
    }
}
