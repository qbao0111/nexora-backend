namespace Nexora.Integrations.Storage;

public sealed class LocalStorageOptions
{
    public const string SectionName = "Storage:Local";
    public string RootPath { get; init; } = ".nexora-storage";
}
