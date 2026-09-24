using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Business.Ai;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Business.Storage;
using Nexora.Data.Billing;
using Nexora.Data.Career;
using Nexora.Data.Persistence;

using static Nexora.Data.Practice.InterviewErrors;

namespace Nexora.Data.Practice;

internal static class InterviewQuestionContracts
{
    internal static bool HasUsableResumeContext(ResumeRecord? resume) => resume is { DeletedAt: null };

    internal static string[]? ReadMissingStarElements(string? evaluation)
    {
        if (string.IsNullOrWhiteSpace(evaluation)) return null;
        try
        {
            using var document = JsonDocument.Parse(evaluation);
            if (!document.RootElement.TryGetProperty("star", out var star) ||
                !star.TryGetProperty("missingElements", out var missing) ||
                missing.ValueKind != JsonValueKind.Array)
                return null;
            return missing.EnumerateArray()
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static void ValidateQuestionContracts(IEnumerable<InterviewQuestion> questions)
    {
        var questionMap = questions.ToDictionary(item => item.Id);
        foreach (var question in questionMap.Values)
        {
            if (!InterviewQuestionValues.IsSupportedKind(question.Kind) ||
                string.IsNullOrWhiteSpace(question.Topic) || question.Topic.Trim().Length > 80)
                throw InvalidState();

            if (string.Equals(question.Kind, InterviewQuestionValues.Primary, StringComparison.Ordinal))
            {
                if (question.ParentQuestionId is not null) throw InvalidState();
                continue;
            }

            if (question.ParentQuestionId is null ||
                !questionMap.TryGetValue(question.ParentQuestionId.Value, out var parent) ||
                parent.Id == question.Id ||
                parent.InterviewSessionId != question.InterviewSessionId ||
                parent.Sequence >= question.Sequence ||
                !string.Equals(parent.Topic, question.Topic, StringComparison.Ordinal))
                throw InvalidState();
        }
    }


}
