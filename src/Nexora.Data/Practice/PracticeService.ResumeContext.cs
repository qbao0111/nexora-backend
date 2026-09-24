using Nexora.Business.Practice;
using Nexora.Data.Persistence;

namespace Nexora.Data.Practice;

public sealed partial class PracticeService
{
    private Task<ResumeProfile> EnsureResumeProfileAsync(ResumeRecord resume, Guid correlationId, CancellationToken cancellationToken) =>
        resumeProfileProcessor.EnsureResumeProfileAsync(resume, correlationId, cancellationToken);

    private static bool HasUsableResumeContext(ResumeRecord? resume) => resume is { DeletedAt: null };

    private static ResumeProfile? TryReadResumeProfile(string? value) => ResumeProfileProcessor.TryReadResumeProfile(value);
}
