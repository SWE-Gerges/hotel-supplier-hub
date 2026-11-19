using Hub.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Hub.Api;

internal static class OpenApiConfiguration
{
    public static IServiceCollection AddHubOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Hotel Supplier Hub API",
                    Version = "v1",
                    Description = "Searches several hotel suppliers behind one contract, merges the answers by canonical hotel and streams them as they arrive.",
                };

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[ApiKeyDefaults.Scheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = ApiKeyDefaults.HeaderName,
                    Description = "API key that identifies the caller, its tenant and its role.",
                };
                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                var secured = context.Description.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>().Any();
                if (secured)
                {
                    operation.Security ??= [];
                    operation.Security.Add(new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(ApiKeyDefaults.Scheme, context.Document)] = [],
                    });
                }

                return Task.CompletedTask;
            });

            options.AddSchemaTransformer((schema, context, _) =>
            {
                if (context.JsonTypeInfo.Type == typeof(SupplierId))
                {
                    schema.Type = JsonSchemaType.String;
                    schema.Properties?.Clear();
                    schema.Pattern = "^[a-z0-9]+(-[a-z0-9]+)*$";
                }

                return Task.CompletedTask;
            });
        });
}
