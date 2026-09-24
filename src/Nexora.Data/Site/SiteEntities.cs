namespace Nexora.Data.Site;

public sealed class SiteSettings
{
    public Guid Id { get; set; }
    public string ContactEmail { get; set; } = string.Empty;
    public string BrandDescription { get; set; } = string.Empty;
    public string? FacebookUrl { get; set; }
    public string? TiktokUrl { get; set; }
    public bool SupportAvailabilityEnabled { get; set; }
    public string? SupportLabel { get; set; }
    public bool MadeInVietnamEnabled { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid ConcurrencyToken { get; set; }
}

public sealed class SitePage
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string DraftTitle { get; set; } = string.Empty;
    public string? DraftBodyMarkdown { get; set; }
    public string? DraftAboutJson { get; set; }
    public DateTimeOffset? DraftEffectiveAt { get; set; }
    public string? PublishedTitle { get; set; }
    public string? PublishedBodyMarkdown { get; set; }
    public string? PublishedAboutJson { get; set; }
    public DateTimeOffset? PublishedEffectiveAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid ConcurrencyToken { get; set; }
}

public sealed class SiteAsset
{
    public Guid Id { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid UploadedBy { get; set; }
}
