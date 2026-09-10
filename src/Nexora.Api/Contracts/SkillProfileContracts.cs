namespace Nexora.Api.Contracts;

public sealed record SkillProfileSourceResponse(
    string SourceType,
    int EvidenceCount,
    DateTimeOffset LatestEvidenceAt);

public sealed record SkillProfileCompetencyResponse(
    string Code,
    string Name,
    string Category,
    int Score,
    int EvidenceCount,
    DateTimeOffset LatestEvidenceAt,
    IReadOnlyCollection<SkillProfileSourceResponse> Sources);

public sealed record SkillProfileWeaknessSignalResponse(
    string SourceType,
    string Label,
    DateTimeOffset LatestEvidenceAt);

public sealed record SkillProfileResponse(
    IReadOnlyCollection<SkillProfileCompetencyResponse> Competencies,
    IReadOnlyCollection<SkillProfileWeaknessSignalResponse> WeaknessSignals);
