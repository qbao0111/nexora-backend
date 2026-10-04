using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nexora.Business.Ai;
using Nexora.Business.Billing;
using Nexora.Business.Practice;
using Nexora.Data.Persistence;
using Nexora.Integrations.Ai;
using UglyToad.PdfPig.Writer;
using static Nexora.IntegrationTests.RealtimeApiTests;

namespace Nexora.IntegrationTests;

[Collection("PostgreSQL primary resume")]
public sealed class ResumeExtractionPrivacyTests
{
    [Fact]
    public Task LocalExtractionThenDeepSeekProfileAndAnalysisPersistWithoutGemini() => RunPipelineAsync(null);

    [PostgresFact]
    public Task LocalExtractionThenDeepSeekProfileAndAnalysisPersistOnPostgres() =>
        RunPipelineAsync(Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!);

    [PostgresFact]
    public async Task UnreadableResumeTerminatesItsOutboxOnPostgresWithoutAi()
    {
        var ai = new TestAiProvider();
        using var factory = NexoraApiFactory.CreatePostgres(
            Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!, ai);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var owner = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        var builder = new PdfDocumentBuilder();
        builder.AddPage(612, 792);
        var bytes = builder.Build();
        var id = await UploadAsync(client, bytes, "cv.pdf", "application/pdf");

        await ProcessJobsAsync(factory);

        using var response = await client.GetAsync($"/api/v1/resumes/{id}");
        var data = await DataAsync(response);
        Assert.Equal("failed", data.GetProperty("status").GetString());
        Assert.Equal("RESUME_EXTRACTION_FAILED", data.GetProperty("errorCode").GetString());
        Assert.Equal(0, ai.TotalCalls);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(BillingValues.Processed, (await db.OutboxEvents.SingleAsync(item => item.AggregateId == id)).Status);
        Assert.Null((await db.Resumes.SingleAsync(item => item.Id == id)).StructuredProfile);
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<IPracticeJobProcessor>().ProcessPendingAsync(CancellationToken.None));
    }

    private static async Task RunPipelineAsync(string? postgresConnection)
    {
        using var handler = new ResumeTextHandler();
        using var http = new HttpClient(handler);
        var ai = new DeepSeekAiProvider(http, Options.Create(new DeepSeekOptions { ApiKey = "test-only-key" }));
        using var factory = postgresConnection is null ? new NexoraApiFactory(ai) : NexoraApiFactory.CreatePostgres(postgresConnection, ai);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var owner = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        using var documentStream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(documentStream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(new Paragraph(new Run(new Text(
                "Backend engineer at Example Company, 2022-2025. Skills C#, PostgreSQL, API design. Education Computer Science.")))));
            main.Document.Save();
        }
        var bytes = documentStream.ToArray();
        var resumeId = await UploadAsync(client, bytes, "cv.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        await ProcessJobsAsync(factory);
        Assert.Equal(0, handler.Calls);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var extracted = await db.Resumes.SingleAsync(item => item.Id == resumeId);
            Assert.Equal("ready", extracted.Status);
            Assert.Contains("Example Company", extracted.ExtractedText, StringComparison.Ordinal);
            Assert.Null(extracted.StructuredProfile);
            var quota = await db.EntitlementFeatures.Include(item => item.Entitlement)
                .SingleAsync(item => item.Entitlement.UserId == owner.UserId && item.FeatureCode == FeatureValues.CvAnalysis);
            quota.IsEnabled = true;
            quota.Limit = 1;
            await db.SaveChangesAsync();
        }
        var jd = await PostAsync(client, "/api/v1/job-descriptions", new { title = "Backend", content = "C# and PostgreSQL development" });
        var analysis = await PostAsync(client, "/api/v1/resume-analyses", new
        {
            resumeId, mode = "job_targeted", jobDescriptionId = jd.GetProperty("id").GetGuid()
        });
        await ProcessJobsAsync(factory);
        using var completed = await client.GetAsync($"/api/v1/resume-analyses/{analysis.GetProperty("id").GetGuid()}");
        Assert.Equal("completed", (await DataAsync(completed)).GetProperty("status").GetString());
        Assert.Equal(2, handler.Calls);
        using var verify = factory.Services.CreateScope();
        var persisted = await verify.ServiceProvider.GetRequiredService<NexoraDbContext>().Resumes.SingleAsync(item => item.Id == resumeId);
        Assert.StartsWith("deepseek:", persisted.ProfileModelVersion, StringComparison.Ordinal);
        Assert.NotNull(persisted.ProfilePromptVersion);
        Assert.NotNull(persisted.ProfileSchemaVersion);
        Assert.Contains("PostgreSQL", persisted.StructuredProfile, StringComparison.Ordinal);
    }

    private static async Task<Guid> UploadAsync(HttpClient client, byte[] bytes, string fileName, string contentType)
    {
        var intent = await PostAsync(client, "/api/v1/uploads/presign", new { fileName, contentType, size = bytes.Length });
        using var upload = new ByteArrayContent(bytes);
        using var uploaded = await client.PutAsync(intent.GetProperty("uploadUrl").GetString(), upload);
        Assert.Equal(HttpStatusCode.NoContent, uploaded.StatusCode);
        var resume = await PostAsync(client, "/api/v1/resumes", new { uploadToken = intent.GetProperty("token").GetString() });
        return resume.GetProperty("id").GetGuid();
    }

    private sealed class ResumeTextHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://api.deepseek.com/chat/completions", request.RequestUri!.AbsoluteUri);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            var messages = json.RootElement.GetProperty("messages");
            Assert.Equal(JsonValueKind.String, messages[1].GetProperty("content").ValueKind);
            var system = messages[0].GetProperty("content").GetString()!;
            var content = Calls++ == 0
                ? """{"summary":"Kỹ sư backend","skills":["C#","PostgreSQL"],"experiences":[],"education":[],"projects":[],"certifications":[],"languages":[]}"""
                : """{"strengths":["Có kỹ năng backend"],"gaps":["Cần mô tả kết quả"],"recommendations":["Bổ sung tác động"],"matchScore":75,"summary":"CV phù hợp định hướng backend","matchedKeywordsOrSkills":["C#"],"missingKeywordsOrSkills":[],"sectionFeedback":["Trình bày rõ hơn kết quả"],"breakdown":{"technicalSkillMatch":75,"experienceRelevance":70,"impactEvidence":65,"clarity":80,"structure":75},"mode":"job_targeted"}""";
            Assert.Contains(Calls == 1 ? "Purpose: resume.profile" : "Purpose: resume.analysis", system, StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content }, finish_reason = "stop" } } }), Encoding.UTF8, "application/json")
            };
        }
    }
}
