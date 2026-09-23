namespace Nexora.Business.Ai;

public static class StarComponentValidator
{
    public static AiValidationResult<StarComponentEvaluation> Validate(StarComponentEvaluation? c, string name)
    {
        if (c is null)
            return AiValidationResult<StarComponentEvaluation>.Success(new StarComponentEvaluation(0, false, string.Empty, $"Không phát hiện thành phần {name} trong câu trả lời."));

        if (c.Score is < 0 or > 100)
            return AiValidationResult<StarComponentEvaluation>.Failure("star.score_out_of_range", "semantic", repairable: true);

        var feedback = string.IsNullOrWhiteSpace(c.Feedback)
            ? (c.Detected ? $"Đã phát hiện thành phần {name} trong câu trả lời." : $"Không phát hiện thành phần {name} trong câu trả lời.")
            : c.Feedback.Trim();

        var evidence = c.Evidence?.Trim() ?? string.Empty;

        if (!c.Detected)
        {
            if (c.Score > 0)
                return AiValidationResult<StarComponentEvaluation>.Failure("star.component_detected_score_mismatch", "semantic", repairable: true);

            if (!string.IsNullOrWhiteSpace(evidence))
                return AiValidationResult<StarComponentEvaluation>.Failure("star.component_detected_score_mismatch", "semantic", repairable: true);

            return AiValidationResult<StarComponentEvaluation>.Success(new StarComponentEvaluation(0, false, string.Empty, feedback));
        }

        // Detected is true
        if (c.Score == 0)
            return AiValidationResult<StarComponentEvaluation>.Failure("star.component_detected_score_mismatch", "semantic", repairable: true);

        if (string.IsNullOrWhiteSpace(evidence))
            return AiValidationResult<StarComponentEvaluation>.Failure("star.component_detected_without_evidence", "semantic", repairable: true);

        return AiValidationResult<StarComponentEvaluation>.Success(new StarComponentEvaluation(c.Score, true, evidence, feedback));
    }
}
