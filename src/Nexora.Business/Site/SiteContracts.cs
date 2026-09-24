using System.ComponentModel.DataAnnotations;
using Nexora.Business.Common;

namespace Nexora.Business.Site;

public sealed record SiteSettingsView(
    string ContactEmail, string BrandDescription, string? FacebookUrl, string? TiktokUrl,
    bool SupportAvailabilityEnabled, string? SupportLabel, bool MadeInVietnamEnabled,
    DateTimeOffset? UpdatedAt, Guid? ConcurrencyToken = null);

public sealed record SiteSettingsWrite(
    string ContactEmail, string BrandDescription, string? FacebookUrl, string? TiktokUrl,
    bool SupportAvailabilityEnabled, string? SupportLabel, bool MadeInVietnamEnabled, Guid? ConcurrencyToken);

public sealed record AboutValue(string Title, string Description, string? IconKey);
public sealed record AboutMilestone(string Label, string Title, string Description);
public sealed record AboutTeamMember(string Name, string Role, string? Bio, Guid? AssetId);
public sealed record AboutContent(
    string HeroTitle, string HeroSubtitle, Guid? HeroAssetId,
    string MissionTitle, string MissionBody, Guid? MissionAssetId,
    IReadOnlyList<AboutValue> Values, IReadOnlyList<AboutMilestone> Milestones,
    bool TeamSectionEnabled, string? TeamHeading, IReadOnlyList<AboutTeamMember> TeamMembers);

public sealed record SitePageView(
    string Key, string Title, string? BodyMarkdown, AboutContent? About,
    DateTimeOffset? EffectiveAt, bool IsPublished, DateTimeOffset? PublishedAt,
    DateTimeOffset? UpdatedAt, Guid? ConcurrencyToken = null);

public sealed record SitePageWrite(
    string Title, string? BodyMarkdown, AboutContent? About,
    DateTimeOffset? EffectiveAt, Guid? ConcurrencyToken);

public sealed record SitePagePublishRequest([param: Required] Guid? ConcurrencyToken);

public sealed record SiteAssetView(Guid Id, string ContentType, long Size, DateTimeOffset CreatedAt);

public interface ISiteContentService
{
    Task<SiteSettingsView> GetSettingsAsync(bool admin, CancellationToken cancellationToken);
    Task<SiteSettingsView> UpdateSettingsAsync(Guid actorId, SiteSettingsWrite write, CancellationToken cancellationToken);
    Task<SitePageView?> GetPageAsync(string key, bool admin, CancellationToken cancellationToken);
    Task<SitePageView> UpdatePageAsync(Guid actorId, string key, SitePageWrite write, CancellationToken cancellationToken);
    Task<SitePageView> PublishPageAsync(Guid actorId, string key, Guid expectedConcurrencyToken, CancellationToken cancellationToken);
    Task<SiteAssetView> UploadAssetAsync(Guid actorId, Stream content, string contentType, CancellationToken cancellationToken);
    Task<(Stream Content, string ContentType)?> OpenAssetAsync(Guid id, bool admin, CancellationToken cancellationToken);
}

public static class SiteContentRules
{
    public const string DefaultEmail = "nexorainterview@gmail.com";
    public const string DefaultDescription = "Luyện phỏng vấn có chủ đích với phản hồi dựa trên bằng chứng.";
    public const int MaximumAssetBytes = 5 * 1024 * 1024;
    public static bool IsPageKey(string key) => key is "about" or "terms" or "privacy";

    public static void ValidateSettings(SiteSettingsWrite write)
    {
        if (string.IsNullOrWhiteSpace(write.ContactEmail) || write.ContactEmail.Length > 254 ||
            !new EmailAddressAttribute().IsValid(write.ContactEmail)) Fail("Email liên hệ không hợp lệ.");
        Text(write.BrandDescription, 500, "Mô tả thương hiệu", true);
        Url(write.FacebookUrl, "Facebook URL");
        Url(write.TiktokUrl, "TikTok URL");
        Text(write.SupportLabel, 100, "Nhãn hỗ trợ");
        if (write.SupportAvailabilityEnabled && string.IsNullOrWhiteSpace(write.SupportLabel))
            Fail("Cần nhãn hỗ trợ khi bật hiển thị.");
    }

    public static void ValidatePage(string key, SitePageWrite write)
    {
        if (!IsPageKey(key)) Fail("Trang không được hỗ trợ.");
        Text(write.Title, 160, "Tiêu đề", true);
        if (key == "about")
        {
            if (write.About is null || !string.IsNullOrWhiteSpace(write.BodyMarkdown)) Fail("Nội dung giới thiệu không hợp lệ.");
            var about = write.About!;
            Text(about.HeroTitle, 160, "Tiêu đề hero", true);
            Text(about.HeroSubtitle, 500, "Mô tả hero", true);
            Text(about.MissionTitle, 160, "Tiêu đề sứ mệnh", true);
            Text(about.MissionBody, 3000, "Nội dung sứ mệnh", true);
            Text(about.TeamHeading, 160, "Tiêu đề đội ngũ");
            if (about.Values is null || about.Values.Count > 6 || about.Milestones is null || about.Milestones.Count > 20 ||
                about.TeamMembers is null || about.TeamMembers.Count > 20) Fail("Số lượng mục giới thiệu vượt giới hạn.");
            foreach (var value in about.Values ?? [])
            {
                Text(value.Title, 100, "Giá trị", true); Text(value.Description, 500, "Mô tả giá trị", true);
                Text(value.IconKey, 40, "Icon");
            }
            foreach (var milestone in about.Milestones ?? [])
            {
                Text(milestone.Label, 100, "Mốc", true); Text(milestone.Title, 160, "Tên mốc", true);
                Text(milestone.Description, 500, "Mô tả mốc", true);
            }
            foreach (var member in about.TeamMembers ?? [])
            {
                Text(member.Name, 120, "Tên thành viên", true); Text(member.Role, 120, "Vai trò", true);
                Text(member.Bio, 500, "Giới thiệu thành viên");
            }
        }
        else
        {
            if (write.About is not null || string.IsNullOrWhiteSpace(write.BodyMarkdown) || write.BodyMarkdown.Length > 30_000)
                Fail("Nội dung tài liệu không hợp lệ.");
            if (write.BodyMarkdown?.Contains('<') == true || write.BodyMarkdown?.Contains('>') == true)
                Fail("Không hỗ trợ HTML trong tài liệu.");
        }
    }

    private static void Text(string? value, int max, string label, bool required = false)
    {
        if ((required && string.IsNullOrWhiteSpace(value)) || value?.Length > max ||
            (value is not null && (value.Contains('<') || value.Contains('>')))) Fail($"{label} không hợp lệ.");
    }

    private static void Url(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) Fail($"{label} không hợp lệ.");
    }

    private static void Fail(string message) => throw new BusinessException("SITE_CONTENT_INVALID", message, BusinessErrorKind.Validation);
}
