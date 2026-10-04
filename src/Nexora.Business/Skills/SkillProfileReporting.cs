using System.Security.Cryptography;
using System.Text.Json;

namespace Nexora.Business.Skills;

public static class SkillProfileReporting
{
    // A versioned reference to the current owner-scoped derived view, not a stored entity.
    // Never treat this hash as authorization; reporting must recompute the owner's view.
    public static Guid? CreateId(Guid userId, SkillProfileView profile)
    {
        if (profile.Competencies.Count == 0 && profile.WeaknessSignals.Count == 0) return null;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = "skill-report-v1",
            userId,
            profile.Competencies,
            profile.WeaknessSignals
        });
        return new Guid(SHA256.HashData(payload).AsSpan(0, 16));
    }
}
