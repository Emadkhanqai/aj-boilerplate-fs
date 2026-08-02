using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace AjBoilerplate.Api.Infrastructure;

/// <summary>
/// Rewrites every operation's declared response schema to show the real wire shape — the
/// <c>ApiResponse&lt;T&gt;</c>/<c>ApiResponse</c> envelope — instead of the bare DTO type that
/// <c>[ProducesResponseType]</c> declares. Runtime wrapping happens in
/// <see cref="EnvelopeResultFilter"/>; this filter only keeps the generated OpenAPI document (and
/// therefore the frontend's generated types) truthful about it.
/// </summary>
public sealed class EnvelopeResponseSchemaFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (operation.Responses is null)
        {
            return;
        }

        foreach (var (statusCode, response) in operation.Responses)
        {
            // 204 is the one success status with no body at all — EnvelopeResultFilter passes it
            // through untouched, so the document must not claim an envelope for it.
            if (statusCode == "204")
            {
                continue;
            }

            var isSuccess = statusCode.StartsWith('2');
            var content = response.Content;

            if (content is null)
            {
                continue;
            }

            if (content.Count == 0)
            {
                // A bare 401/404 with no typed body (error) or a void 200/201 (success, data: null)
                // — both still get an envelope on the wire.
                content["application/json"] = new OpenApiMediaType { Schema = isSuccess ? SuccessSchema(null) : ErrorSchema() };
                continue;
            }

            foreach (var key in content.Keys.ToList())
            {
                var original = content[key].Schema;
                content[key].Schema = isSuccess ? SuccessSchema(original) : ErrorSchema();
            }
        }
    }

    private static OpenApiSchema SuccessSchema(IOpenApiSchema? dataSchema) => new()
    {
        Type = JsonSchemaType.Object,
        Properties = new Dictionary<string, IOpenApiSchema>
        {
            ["success"] = new OpenApiSchema { Type = JsonSchemaType.Boolean },
            ["data"] = dataSchema ?? new OpenApiSchema(),
            ["message"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
            ["errors"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Array | JsonSchemaType.Null,
                Items = new OpenApiSchema { Type = JsonSchemaType.String },
            },
            ["statusCode"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
            ["code"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
            ["timestamp"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "date-time" },
            ["traceId"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
        },
    };

    private static OpenApiSchema ErrorSchema() => new()
    {
        Type = JsonSchemaType.Object,
        Properties = new Dictionary<string, IOpenApiSchema>
        {
            ["success"] = new OpenApiSchema { Type = JsonSchemaType.Boolean, Default = System.Text.Json.Nodes.JsonValue.Create(false) },
            ["message"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
            ["errors"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Array | JsonSchemaType.Null,
                Items = new OpenApiSchema { Type = JsonSchemaType.String },
            },
            ["code"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
            ["timestamp"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "date-time" },
            ["traceId"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
        },
    };
}
