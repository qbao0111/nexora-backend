using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Nexora.Api.Infrastructure;

internal static class OpenApiConfiguration
{
    public static void Configure(OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "Nexora API";
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Paste data.accessToken from register/login, without the Bearer prefix."
            };
            return Task.CompletedTask;
        });
        options.AddOperationTransformer((operation, context, _) =>
        {
            var metadata = context.Description.ActionDescriptor.EndpointMetadata;
            if (metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any())
                operation.Security = [new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = []
                }];

            // Document headers read directly from Request; business validation remains authoritative.
            var path = context.Description.RelativePath;
            if (context.Description.HttpMethod == "POST" && path is
                "api/v1/checkout-sessions" or "api/v1/resume-analyses" or "api/v1/interviews" or
                "api/v1/interviews/{id}/answers" or "api/v1/interviews/{id}/complete" or
                "api/v1/me/deletion-requests" or "api/v1/dev/resume-analysis" or
                "api/v1/star-attempts" or
                "api/v1/scenario-attempts" or "api/v1/scenario-attempts/{id}/submit" or
                "api/v1/admin/users/{userId}/plan-grants" or "api/v1/admin/users/{userId}/feature-adjustments")
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "Idempotency-Key",
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "One unique key per action. Reuse the same key and body for retries only.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                });
            }

            // This endpoint reads raw Request.Body, not JSON or multipart/form-data.
            if (context.Description.HttpMethod == "PUT" && path == "api/v1/uploads/{token}")
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Description = "Select the same PDF/DOCX declared in uploads/presign. The token is a short-lived upload capability.",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/pdf"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } },
                        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = new()
                        {
                            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" }
                        }
                    }
                };
            return Task.CompletedTask;
        });
    }
}
