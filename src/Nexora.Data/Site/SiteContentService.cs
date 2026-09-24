using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Business.Common;
using Nexora.Business.Site;
using Nexora.Business.Storage;
using Nexora.Data.Billing;
using Nexora.Data.Persistence;
using Npgsql;

namespace Nexora.Data.Site;

public sealed partial class SiteContentService(NexoraDbContext db, IStorageProvider storage, TimeProvider clock, ILogger<SiteContentService> logger) : ISiteContentService
{
    private static readonly Guid SettingsId = Guid.Parse("a65a1dbd-782d-47f4-a8eb-9d7e61064e23");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SiteSettingsView> GetSettingsAsync(bool admin, CancellationToken cancellationToken)
    {
        var settings = await db.SiteSettings.AsNoTracking().SingleOrDefaultAsync(item => item.Id == SettingsId, cancellationToken);
        return settings is null
            ? new SiteSettingsView(SiteContentRules.DefaultEmail, SiteContentRules.DefaultDescription, null, null, false, null, true, null)
            : Map(settings, admin);
    }

    public async Task<SiteSettingsView> UpdateSettingsAsync(Guid actorId, SiteSettingsWrite write, CancellationToken cancellationToken)
    {
        SiteContentRules.ValidateSettings(write);
        var settings = await db.SiteSettings.SingleOrDefaultAsync(item => item.Id == SettingsId, cancellationToken);
        var creating = settings is null;
        if (settings is null)
        {
            if (write.ConcurrencyToken.HasValue) throw Conflict();
            settings = new SiteSettings { Id = SettingsId };
            db.SiteSettings.Add(settings);
        }
        else if (write.ConcurrencyToken != settings.ConcurrencyToken) throw Conflict();

        settings.ContactEmail = write.ContactEmail.Trim();
        settings.BrandDescription = write.BrandDescription.Trim();
        settings.FacebookUrl = EmptyToNull(write.FacebookUrl);
        settings.TiktokUrl = EmptyToNull(write.TiktokUrl);
        settings.SupportAvailabilityEnabled = write.SupportAvailabilityEnabled;
        settings.SupportLabel = write.SupportAvailabilityEnabled ? EmptyToNull(write.SupportLabel) : null;
        settings.MadeInVietnamEnabled = write.MadeInVietnamEnabled;
        settings.UpdatedAt = clock.GetUtcNow();
        settings.ConcurrencyToken = Guid.NewGuid();
        Audit(actorId, "site.settings.update", "site_settings", settings.Id);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { throw Conflict(); }
        catch (DbUpdateException exception) when (creating && IsSiteInsertConflict(exception, "PK_site_settings", "site_settings.Id"))
        {
            throw Conflict();
        }
        return Map(settings, true);
    }

    public async Task<SitePageView?> GetPageAsync(string key, bool admin, CancellationToken cancellationToken)
    {
        CheckKey(key);
        var page = await db.SitePages.AsNoTracking().SingleOrDefaultAsync(item => item.Key == key, cancellationToken);
        if (page is null || (!admin && page.PublishedAt is null)) return null;
        return Map(page, admin);
    }

    public async Task<SitePageView> UpdatePageAsync(Guid actorId, string key, SitePageWrite write, CancellationToken cancellationToken)
    {
        CheckKey(key);
        SiteContentRules.ValidatePage(key, write);
        await CheckAssetsAsync(write.About, cancellationToken);
        var page = await db.SitePages.SingleOrDefaultAsync(item => item.Key == key, cancellationToken);
        var creating = page is null;
        if (page is null)
        {
            if (write.ConcurrencyToken.HasValue) throw Conflict();
            page = new SitePage { Id = Guid.NewGuid(), Key = key };
            db.SitePages.Add(page);
        }
        else if (write.ConcurrencyToken != page.ConcurrencyToken) throw Conflict();

        page.DraftTitle = write.Title.Trim();
        page.DraftBodyMarkdown = write.BodyMarkdown?.Trim();
        page.DraftAboutJson = write.About is null ? null : JsonSerializer.Serialize(write.About, JsonOptions);
        page.DraftEffectiveAt = write.EffectiveAt;
        page.UpdatedAt = clock.GetUtcNow();
        page.ConcurrencyToken = Guid.NewGuid();
        Audit(actorId, $"site.page.{key}.update", "site_page", page.Id);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { throw Conflict(); }
        catch (DbUpdateException exception) when (creating && IsSiteInsertConflict(exception, "IX_site_pages_Key", "site_pages.Key"))
        {
            throw Conflict();
        }
        return Map(page, true);
    }

    public async Task<SitePageView> PublishPageAsync(Guid actorId, string key, Guid expectedConcurrencyToken, CancellationToken cancellationToken)
    {
        CheckKey(key);
        var page = await db.SitePages.SingleOrDefaultAsync(item => item.Key == key, cancellationToken)
            ?? throw new BusinessException("NOT_FOUND", "Trang chưa có bản nháp.", BusinessErrorKind.NotFound);
        if (expectedConcurrencyToken != page.ConcurrencyToken) throw Conflict();
        if (key == "about" && page.DraftAboutJson is null || key != "about" && page.DraftBodyMarkdown is null)
            throw new BusinessException("SITE_CONTENT_INVALID", "Bản nháp chưa hoàn chỉnh.", BusinessErrorKind.Validation);
        page.PublishedTitle = page.DraftTitle;
        page.PublishedBodyMarkdown = page.DraftBodyMarkdown;
        page.PublishedAboutJson = page.DraftAboutJson;
        page.PublishedEffectiveAt = page.DraftEffectiveAt;
        page.PublishedAt = clock.GetUtcNow();
        page.UpdatedAt = page.PublishedAt.Value;
        page.ConcurrencyToken = Guid.NewGuid();
        Audit(actorId, $"site.page.{key}.publish", "site_page", page.Id);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { throw Conflict(); }
        return Map(page, true);
    }

    public async Task<SiteAssetView> UploadAssetAsync(Guid actorId, Stream content, string contentType, CancellationToken cancellationToken)
    {
        if (contentType is not ("image/jpeg" or "image/png" or "image/webp")) InvalidAsset();
        await using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > SiteContentRules.MaximumAssetBytes) InvalidAsset();
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        var bytes = buffer.ToArray();
        if (bytes.Length == 0 || !MatchesMagic(bytes, contentType)) InvalidAsset();
        buffer.Position = 0;
        var extension = contentType switch { "image/jpeg" => "jpg", "image/png" => "png", _ => "webp" };
        var stored = await storage.SaveAsync(buffer, $"site-{Guid.NewGuid():N}.{extension}", contentType, cancellationToken);
        var asset = new SiteAsset
        {
            Id = Guid.NewGuid(),
            StorageKey = stored.StorageKey,
            ContentType = contentType,
            Size = bytes.LongLength,
            CreatedAt = clock.GetUtcNow(),
            UploadedBy = actorId
        };
        try
        {
            db.SiteAssets.Add(asset);
            Audit(actorId, "site.asset.upload", "site_asset", asset.Id);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await storage.DeleteAsync(stored.StorageKey, cancellationToken);
            throw;
        }
        return new SiteAssetView(asset.Id, asset.ContentType, asset.Size, asset.CreatedAt);
    }

    public async Task<(Stream Content, string ContentType)?> OpenAssetAsync(Guid id, bool admin, CancellationToken cancellationToken)
    {
        var asset = await db.SiteAssets.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (asset is null) return null;
        if (!admin)
        {
            var publishedAbout = await db.SitePages.AsNoTracking()
                .Where(page => page.Key == "about" && page.PublishedAt != null)
                .Select(page => page.PublishedAboutJson)
                .SingleOrDefaultAsync(cancellationToken);
            if (publishedAbout is null) return null;
            var about = JsonSerializer.Deserialize<AboutContent>(publishedAbout, JsonOptions);
            var referenced = about is not null && (about.HeroAssetId == id || about.MissionAssetId == id ||
                about.TeamSectionEnabled && about.TeamMembers.Any(member => member.AssetId == id));
            if (!referenced) return null;
        }
        try
        {
            return (await storage.OpenReadAsync(asset.StorageKey, cancellationToken), asset.ContentType);
        }
        catch (FileNotFoundException)
        {
            SiteAssetMissing(logger, id, storage.GetType().Name);
            return null;
        }
    }

    [LoggerMessage(LogLevel.Information, "Stored object not found: domain=site_asset provider={Provider} assetId={AssetId}")]
    private static partial void SiteAssetMissing(ILogger logger, Guid assetId, string provider);

    private async Task CheckAssetsAsync(AboutContent? about, CancellationToken cancellationToken)
    {
        if (about is null) return;
        var ids = new Guid?[] { about.HeroAssetId, about.MissionAssetId }.Concat(about.TeamMembers.Select(member => member.AssetId))
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        var count = await db.SiteAssets.CountAsync(asset => ids.Contains(asset.Id), cancellationToken);
        if (count != ids.Length) throw new BusinessException("SITE_CONTENT_INVALID", "Ảnh website không tồn tại.", BusinessErrorKind.Validation);
    }

    private static bool MatchesMagic(byte[] bytes, string contentType) => contentType switch
    {
        "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
        "image/png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/webp" => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
        _ => false
    };

    private static SiteSettingsView Map(SiteSettings value, bool admin) => new(value.ContactEmail, value.BrandDescription,
        value.FacebookUrl, value.TiktokUrl, value.SupportAvailabilityEnabled, value.SupportLabel,
        value.MadeInVietnamEnabled, value.UpdatedAt, admin ? value.ConcurrencyToken : null);

    private static SitePageView Map(SitePage value, bool admin) => new(value.Key,
        admin ? value.DraftTitle : value.PublishedTitle!, admin ? value.DraftBodyMarkdown : value.PublishedBodyMarkdown,
        JsonSerializer.Deserialize<AboutContent?>(admin ? value.DraftAboutJson ?? "null" : value.PublishedAboutJson ?? "null", JsonOptions),
        admin ? value.DraftEffectiveAt : value.PublishedEffectiveAt,
        value.PublishedAt.HasValue, value.PublishedAt, admin ? value.UpdatedAt : value.PublishedAt,
        admin ? value.ConcurrencyToken : null);

    private void Audit(Guid actorId, string action, string targetType, Guid targetId) => db.AdminAuditEvents.Add(new AdminAuditEvent
    {
        Id = Guid.NewGuid(),
        AdminUserId = actorId,
        Action = action,
        TargetType = targetType,
        TargetId = targetId.ToString("N"),
        Reason = "site_content",
        CreatedAt = clock.GetUtcNow()
    });

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static void CheckKey(string key)
    {
        if (!SiteContentRules.IsPageKey(key)) throw new BusinessException("NOT_FOUND", "Trang không tồn tại.", BusinessErrorKind.NotFound);
    }
    private static bool IsSiteInsertConflict(DbUpdateException exception, string postgresConstraint, string sqliteColumn) =>
        exception.InnerException is PostgresException postgres && postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
        postgres.ConstraintName == postgresConstraint ||
        exception.InnerException?.GetType().FullName == "Microsoft.Data.Sqlite.SqliteException" &&
        exception.InnerException.Message.Contains($"UNIQUE constraint failed: {sqliteColumn}", StringComparison.Ordinal);

    private static BusinessException Conflict() => new("SITE_CONTENT_CONFLICT", "Nội dung đã thay đổi, vui lòng tải lại.", BusinessErrorKind.Conflict);
    private static void InvalidAsset() => throw new BusinessException("SITE_ASSET_INVALID", "Ảnh không hợp lệ hoặc vượt giới hạn 5 MiB.", BusinessErrorKind.Validation);
}
