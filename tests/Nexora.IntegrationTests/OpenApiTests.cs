using System.Net;
using System.Text.Json;

namespace Nexora.IntegrationTests;

public sealed class OpenApiTests
{
    [Theory]
    [InlineData("Development", HttpStatusCode.OK, HttpStatusCode.OK)]
    [InlineData("Testing", HttpStatusCode.NotFound, HttpStatusCode.OK)]
    [InlineData("Staging", HttpStatusCode.OK, HttpStatusCode.OK)]
    [InlineData("Production", HttpStatusCode.NotFound, HttpStatusCode.NotFound)]
    public async Task SwaggerIsDevelopmentAndStagingOnly(string environment, HttpStatusCode uiStatus, HttpStatusCode documentStatus)
    {
        await using var factory = new NexoraApiFactory(environment);
        using var client = factory.CreateHttpsClient();
        using var index = await client.GetAsync("/swagger/index.html");
        Assert.Equal(uiStatus, index.StatusCode);
        using var script = await client.GetAsync("/swagger/swagger-ui-bundle.js");
        Assert.Equal(uiStatus, script.StatusCode);
        using var document = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(documentStatus, document.StatusCode);
        if (environment is "Development" or "Staging")
        {
            using var initializer = await client.GetAsync("/swagger/index.js");
            var configuration = await initializer.Content.ReadAsStringAsync();
            Assert.Contains("../openapi/v1.json", configuration);
            Assert.Contains("\"persistAuthorization\":false", configuration);
            Assert.Contains("\"validatorUrl\":\"\"", configuration);
            Assert.Contains("swagger-ui", await index.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task OpenApiDescribesBearerIdempotencyAndRawUploads()
    {
        await using var factory = new NexoraApiFactory();
        using var client = factory.CreateHttpsClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var bearer = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
        var paths = root.GetProperty("paths");
        Assert.True(paths.GetProperty("/api/v1/me").GetProperty("get").GetProperty("security")[0].TryGetProperty("Bearer", out _));
        Assert.False(paths.GetProperty("/api/v1/auth/login").GetProperty("post").TryGetProperty("security", out _));
        Assert.False(paths.GetProperty("/api/v1/plans").GetProperty("get").TryGetProperty("security", out _));
        foreach (var path in new[] { "/api/v1/checkout-sessions", "/api/v1/resume-analyses", "/api/v1/interviews",
                     "/api/v1/interviews/{id}/answers", "/api/v1/interviews/{id}/complete", "/api/v1/me/deletion-requests",
                     "/api/v1/interviews/{id}/report/retry", "/api/v1/dev/resume-analysis" })
        {
            var header = Assert.Single(paths.GetProperty(path).GetProperty("post").GetProperty("parameters").EnumerateArray(),
                parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key");
            Assert.Equal("header", header.GetProperty("in").GetString());
            Assert.True(header.GetProperty("required").GetBoolean());
        }
        var upload = paths.GetProperty("/api/v1/uploads/{token}").GetProperty("put");
        Assert.False(upload.TryGetProperty("security", out _));
        var content = upload.GetProperty("requestBody").GetProperty("content");
        Assert.Equal("binary", content.GetProperty("application/pdf").GetProperty("schema").GetProperty("format").GetString());
        Assert.True(content.TryGetProperty("application/vnd.openxmlformats-officedocument.wordprocessingml.document", out _));
        Assert.False(content.TryGetProperty("multipart/form-data", out _));

        var shortcut = paths.GetProperty("/api/v1/dev/resume-analysis").GetProperty("post");
        var shortcutBody = shortcut.GetProperty("requestBody").GetProperty("content").GetProperty("multipart/form-data");
        var shortcutProperties = shortcutBody.GetProperty("schema").GetProperty("properties");
        Assert.True(shortcutProperties.TryGetProperty("File", out _));
        Assert.True(shortcutProperties.TryGetProperty("JobDescription", out _));
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task DevelopmentShortcutIsNotMappedOutsideDevelopment(string environment)
    {
        await using var factory = new NexoraApiFactory(environment);
        using var client = factory.CreateHttpsClient();
        using var form = new MultipartFormDataContent();
        using var response = await client.PostAsync("/api/v1/dev/resume-analysis", form);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
