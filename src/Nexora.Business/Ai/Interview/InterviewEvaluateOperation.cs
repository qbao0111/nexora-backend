using System.Text.Json;
using Nexora.Business.Practice;

namespace Nexora.Business.Ai;

public sealed class InterviewEvaluateOperation : AiOperationDefinition<AnswerEvaluation>
{
    public override string Purpose => AiPurposes.InterviewEvaluate;
    public override string PromptVersion => "interview-eval-v11";
    public override string SchemaVersion => "interview-eval-v7";
    public override string RubricVersion => "rubric-v2";
    public override int MaxOutputTokens => 6_000;
    public override bool SupportsOutputTruncationRetry => true;
    public override int GetEffectiveMaxOutputTokens(int attempt, bool outputTruncationRetry) =>
        ValidateEffectiveMaxOutputTokens(attempt == 2 && outputTruncationRetry ? 8_192 : MaxOutputTokens);
    public override AiReasoningEffortOverride? GetRecoveryReasoningOverride(
        string modelVersion,
        AiRecoveryReason reason) =>
        modelVersion.StartsWith("deepseek:", StringComparison.OrdinalIgnoreCase) &&
        reason is (AiRecoveryReason.SemanticValidation or
            AiRecoveryReason.MalformedStructuredOutput or
            AiRecoveryReason.OutputTruncated)
            ? AiReasoningEffortOverride.Disabled
            : null;

    public override JsonDocument OutputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "scoreScale": { "type": "string", "enum": ["0-100"] },
            "scores": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "criterion": { "type": "string", "enum": ["correctness", "structure", "completeness", "clarity"] },
                  "score": { "type": "integer", "minimum": 0, "maximum": 100 },
                  "evidence": { "type": "string", "maxLength": 300 }
                },
                "required": ["criterion", "score", "evidence"]
              }
            },
            "feedback": { "type": "string", "maxLength": 700 },
            "strengths": { "type": "array", "minItems": 0, "maxItems": 3, "items": { "type": "string", "minLength": 1, "maxLength": 240 } },
            "improvements": { "type": "array", "minItems": 1, "maxItems": 3, "items": { "type": "string", "minLength": 1, "maxLength": 240 } },
            "improvedAnswer": { "type": "string", "minLength": 1, "maxLength": 1200 },
            "sampleAnswer": {
              "type": "object",
              "nullable": true,
              "additionalProperties": false,
              "properties": {
                "framework": { "type": "string", "enum": ["star", "self_intro", "technical", "direct"] },
                "situation": { "type": "string", "nullable": true, "maxLength": 350 },
                "task": { "type": "string", "nullable": true, "maxLength": 350 },
                "action": { "type": "string", "nullable": true, "maxLength": 500 },
                "result": { "type": "string", "nullable": true, "maxLength": 350 },
                "fullAnswer": { "type": "string", "minLength": 1, "maxLength": 1200 }
              },
              "required": ["framework", "situation", "task", "action", "result", "fullAnswer"]
            },
            "star": {
              "type": "object",
              "properties": {
                "applicable": { "type": "boolean" },
                "overallScore": { "type": "integer", "minimum": 0, "maximum": 100, "nullable": true },
                "situation": {
                  "type": "object",
                  "properties": {
                    "score": { "type": "integer", "minimum": 0, "maximum": 100 },
                    "detected": { "type": "boolean" },
                    "evidence": { "type": "string", "maxLength": 300 },
                    "feedback": { "type": "string", "maxLength": 240 }
                  },
                  "required": ["score", "detected", "evidence", "feedback"]
                },
                "task": {
                  "type": "object",
                  "properties": {
                    "score": { "type": "integer", "minimum": 0, "maximum": 100 },
                    "detected": { "type": "boolean" },
                    "evidence": { "type": "string", "maxLength": 300 },
                    "feedback": { "type": "string", "maxLength": 240 }
                  },
                  "required": ["score", "detected", "evidence", "feedback"]
                },
                "action": {
                  "type": "object",
                  "properties": {
                    "score": { "type": "integer", "minimum": 0, "maximum": 100 },
                    "detected": { "type": "boolean" },
                    "evidence": { "type": "string", "maxLength": 300 },
                    "feedback": { "type": "string", "maxLength": 240 }
                  },
                  "required": ["score", "detected", "evidence", "feedback"]
                },
                "result": {
                  "type": "object",
                  "properties": {
                    "score": { "type": "integer", "minimum": 0, "maximum": 100 },
                    "detected": { "type": "boolean" },
                    "evidence": { "type": "string", "maxLength": 300 },
                    "feedback": { "type": "string", "maxLength": 240 }
                  },
                  "required": ["score", "detected", "evidence", "feedback"]
                },
                "missingElements": { "type": "array", "maxItems": 4, "items": { "type": "string", "maxLength": 80 } },
                "strengths": { "type": "array", "maxItems": 3, "items": { "type": "string", "maxLength": 200 } },
                "coachingTips": { "type": "array", "maxItems": 3, "items": { "type": "string", "maxLength": 240 } }
              },
              "required": ["applicable"]
            }
          },
          "required": ["scoreScale", "scores", "feedback", "strengths", "improvements", "improvedAnswer"]
        }
        """);

    public override string Instructions =>
        $"""
        Evaluate the candidate's answer against the job and question requirements.
        Set scoreScale to '0-100'.
        Produce compact JSON: short direct evidence quotes, one brief point per feedback/strength/improvement, a concise grounded improvedAnswer, and a short optional teaching sample. Avoid repeating advice across fields; finish every required field before adding optional detail.
        Return exactly four rubric scores for criteria: correctness, structure, completeness, clarity (scores 0-100 with non-empty evidence quote).
        Return 1-3 modest strengths grounded only in direct evidence from the candidate's submitted answer. Each strength must reuse at least one concrete phrase, technology, action, fact, or result from that answer. The supplied question and context may inform relevance and rubric scoring, but they are not evidence that the candidate stated or performed anything. Prefer wording such as 'Bạn đã nêu rõ...' or 'Bạn mô tả cụ thể...'. Do not infer leadership, ownership, production experience, business impact, mentoring, scale, team size, architecture ownership, deployment success, or measurable outcomes unless the candidate explicitly states them. If no grounded positive evidence is demonstrated, return an empty strengths array, keep rubric scores below 60 where justified, and do not invent a strength.
        Return 1-3 improvements, and make every item a direct action the candidate can take. Start with or clearly include a substantive action verb such as add, include, explain, quantify, clarify, describe, mention, specify, show, provide, use, connect, highlight, focus, compare, give, identify, emphasize, present, tập trung, trình bày, làm nổi bật, liên hệ, đưa ví dụ, chỉ ra, nhấn mạnh, so sánh, giải thích, mô tả, làm rõ, bổ sung, nêu, định lượng, or cụ thể hóa. Directive prefixes such as 'hãy', 'nên', or 'có thể' may introduce an action, but do not count by themselves. Do not return passive observations such as 'the result is unclear'. Return one improvedAnswer.
        Keep improvedAnswer faithful to the candidate answer: do not add metrics, achievements, technologies, roles, or experience that are not explicitly present. When evidence is missing, explain what concrete evidence the candidate could add instead of inventing it. Use the answer's facts; do not call another AI operation to rewrite it.
        {AiLanguagePolicy.VietnameseUserFacingInstruction}

        If the question is technical or non-behavioral:
        Set star.applicable = false, omit component details.

        If the question is behavioral:
        Set star.applicable = true.
        {StarSemantics.CanonicalInstructions}

        Return a separate optional sampleAnswer for teaching. This is an EXAMPLE, not a claim about the real candidate. You may invent realistic hypothetical details for teaching, but keep them plausible and concise, clearly confined to sampleAnswer, and never imply they belong to this candidate. Never copy sampleAnswer details into rubric evidence, STAR evaluation evidence, scores, feedback, strengths, improvements, improvedAnswer, the candidate transcript, or downstream candidate facts. The submitted candidate answer remains the only factual evidence source. If no usable example can be produced, return sampleAnswer=null; an invalid sample must not compromise otherwise-valid evaluation fields.
        Choose exactly one framework enum: star, self_intro, technical, or direct. Use star for questions about past experience, behavioral evidence, conflict, leadership, teamwork, experience-based problem-solving, situational/project examples, or achievements. Use self_intro for self-introduction, motivation, or role-fit questions. Use technical for technical-knowledge questions. Use direct for other questions. Do not mechanically force STAR onto every question.
        For framework=star, provide nonblank Situation, Task, Action, and Result sections. Situation is concise context; Task is the candidate's responsibility/objective; Action is the most detailed section with concrete individual decisions/actions rather than vague 'we did'; Result is an outcome, plausible measurement where useful, learning, or impact. Avoid absurdly precise fake metrics. FullAnswer must read naturally as one concise professional Vietnamese interview response, not four disconnected bullets.
        For framework=self_intro, set Situation, Task, Action, and Result to null; compose a natural concise introduction from current positioning, relevant background, strongest relevant capability, and reason for fit/direction, without fake STAR labels. For framework=technical, set all STAR fields to null; answer directly, explain the principle, provide a concrete example, and mention a tradeoff/caveat when useful. Phrase hypotheticals as 'Ví dụ, trong một hệ thống...', not as personal candidate experience unless the submitted answer says so. For framework=direct, set all STAR fields to null and answer naturally without forcing another structure.
        """;

    public override AiValidationResult<AnswerEvaluation> NormalizeAndValidate(AnswerEvaluation? raw, AiOperationContext context)
    {
        if (raw is null)
            return AiValidationResult<AnswerEvaluation>.Failure("rubric.criteria_missing", "semantic", repairable: true);
        if (!string.Equals(raw.ScoreScale, AiOperations.ScoreScale, StringComparison.Ordinal))
            return AiValidationResult<AnswerEvaluation>.Failure("score.scale_invalid", "semantic", repairable: true);

        var sampleAnswer = NormalizeSampleAnswer(raw.SampleAnswer);

        var rubricResult = CanonicalRubricValidator.ValidateAndNormalize(raw.Scores);
        if (!rubricResult.IsValid)
            return AiValidationResult<AnswerEvaluation>.Failure(rubricResult.FailureReason!, rubricResult.ValidationStage!, rubricResult.Repairable);

        if (raw.SampleAnswer is not null && !string.IsNullOrWhiteSpace(context.CandidateAnswer) &&
            rubricResult.NormalizedValue!.Any(score =>
                IsIllustrativeSampleEvidence(score.Evidence, context.CandidateAnswer, raw.SampleAnswer)))
        {
            return AiValidationResult<AnswerEvaluation>.Failure("interview.rubric_evidence_ungrounded", "semantic", repairable: true);
        }

        if (string.IsNullOrWhiteSpace(raw.Feedback))
            return AiValidationResult<AnswerEvaluation>.Failure("interview.feedback_blank", "semantic", repairable: true);
        var feedback = raw.Feedback.Trim();

        // Server authoritative STAR validation
        var expectedStar = context.ExpectedStar ?? false;
        StarEvaluation normalizedStar;

        if (!expectedStar)
        {
            // If server expects non-behavioral, normalize to false regardless of model opinion
            normalizedStar = new StarEvaluation(
                false,
                null,
                null,
                null,
                null,
                null,
                [],
                [],
                raw.Star?.CoachingTips?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? [],
                AiOperations.ScoreScale);
        }
        else
        {
            // Server expects STAR=true
            if (raw.Star is null)
                return AiValidationResult<AnswerEvaluation>.Failure("star.missing", "semantic", repairable: true);

            if (!raw.Star.Applicable)
                return AiValidationResult<AnswerEvaluation>.Failure("star.applicability_mismatch", "semantic", repairable: true);

            var sitResult = StarComponentValidator.Validate(raw.Star.Situation, "situation");
            if (!sitResult.IsValid)
                return AiValidationResult<AnswerEvaluation>.Failure(sitResult.FailureReason!, sitResult.ValidationStage!, sitResult.Repairable);

            var taskResult = StarComponentValidator.Validate(raw.Star.Task, "task");
            if (!taskResult.IsValid)
                return AiValidationResult<AnswerEvaluation>.Failure(taskResult.FailureReason!, taskResult.ValidationStage!, taskResult.Repairable);

            var actResult = StarComponentValidator.Validate(raw.Star.Action, "action");
            if (!actResult.IsValid)
                return AiValidationResult<AnswerEvaluation>.Failure(actResult.FailureReason!, actResult.ValidationStage!, actResult.Repairable);

            var resResult = StarComponentValidator.Validate(raw.Star.Result, "result");
            if (!resResult.IsValid)
                return AiValidationResult<AnswerEvaluation>.Failure(resResult.FailureReason!, resResult.ValidationStage!, resResult.Repairable);

            var sit = sitResult.NormalizedValue!;
            var task = taskResult.NormalizedValue!;
            var act = actResult.NormalizedValue!;
            var res = resResult.NormalizedValue!;

            if (raw.SampleAnswer is not null && !string.IsNullOrWhiteSpace(context.CandidateAnswer) &&
                ((sit.Detected && IsIllustrativeSampleEvidence(sit.Evidence, context.CandidateAnswer, raw.SampleAnswer)) ||
                 (task.Detected && IsIllustrativeSampleEvidence(task.Evidence, context.CandidateAnswer, raw.SampleAnswer)) ||
                 (act.Detected && IsIllustrativeSampleEvidence(act.Evidence, context.CandidateAnswer, raw.SampleAnswer)) ||
                 (res.Detected && IsIllustrativeSampleEvidence(res.Evidence, context.CandidateAnswer, raw.SampleAnswer))))
            {
                return AiValidationResult<AnswerEvaluation>.Failure("star.evidence_ungrounded", "semantic", repairable: true);
            }

            // Authoritative server-computed overall score: Situation 20%, Task 20%, Action 35%, Result 25%
            var overallScore = (int)Math.Round(sit.Score * 0.20 + task.Score * 0.20 + act.Score * 0.35 + res.Score * 0.25);

            // Recompute missingElements strictly from normalized server component state
            var missing = new[] { ("situation", sit), ("task", task), ("action", act), ("result", res) }
                .Where(x => !x.Item2.Detected || x.Item2.Score < 60)
                .Select(x => x.Item1)
                .ToArray();

            normalizedStar = new StarEvaluation(
                true,
                overallScore,
                sit,
                task,
                act,
                res,
                missing,
                raw.Star.Strengths?.Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim())
                    .Where(s => raw.SampleAnswer is null || string.IsNullOrWhiteSpace(context.CandidateAnswer) ||
                        AnswerCoachingValidator.IsGroundedReportStrength(s, context.CandidateAnswer))
                    .Take(3).ToArray() ?? [],
                raw.Star.CoachingTips?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? [],
                AiOperations.ScoreScale);
        }

        var coachingResult = AnswerCoachingValidator.ValidateAndNormalize(
            raw.Strengths,
            raw.Improvements,
            raw.ImprovedAnswer,
            context.CandidateAnswer,
            rubricResult.NormalizedValue!);
        if (!coachingResult.IsValid)
            return AiValidationResult<AnswerEvaluation>.Failure(coachingResult.FailureReason!, coachingResult.ValidationStage!, coachingResult.Repairable);

        return AiValidationResult<AnswerEvaluation>.Success(
            new AnswerEvaluation(
                rubricResult.NormalizedValue!,
                feedback,
                normalizedStar,
                AiOperations.ScoreScale,
                coachingResult.NormalizedValue!.Strengths,
                coachingResult.NormalizedValue.Improvements,
                coachingResult.NormalizedValue.ImprovedAnswer,
                sampleAnswer));
    }

    private static SampleInterviewAnswer? NormalizeSampleAnswer(SampleInterviewAnswer? raw)
    {
        if (raw is null || string.IsNullOrWhiteSpace(raw.Framework) || string.IsNullOrWhiteSpace(raw.FullAnswer))
            return null;

        var framework = raw.Framework.Trim();
        if (framework is not ("star" or "self_intro" or "technical" or "direct"))
            return null;

        var fullAnswer = raw.FullAnswer.Trim();
        if (fullAnswer.Length > 4_000)
            return null;

        if (framework != "star")
            return new SampleInterviewAnswer(framework, null, null, null, null, fullAnswer);

        var situation = NormalizeSampleSection(raw.Situation);
        var task = NormalizeSampleSection(raw.Task);
        var action = NormalizeSampleSection(raw.Action);
        var result = NormalizeSampleSection(raw.Result);
        if (situation is null || task is null || action is null || result is null)
            return null;

        return new SampleInterviewAnswer(framework, situation, task, action, result, fullAnswer);
    }

    private static string? NormalizeSampleSection(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        return normalized.Length <= 1_200 ? normalized : null;
    }

    private static bool IsIllustrativeSampleEvidence(
        string evidence,
        string candidateAnswer,
        SampleInterviewAnswer sampleAnswer)
    {
        if (AnswerCoachingValidator.IsGroundedReportEvidence(evidence, candidateAnswer))
            return false;

        var illustrativeText = string.Join(
            " ",
            new[]
            {
                sampleAnswer.Situation,
                sampleAnswer.Task,
                sampleAnswer.Action,
                sampleAnswer.Result,
                sampleAnswer.FullAnswer
            }.Where(section => !string.IsNullOrWhiteSpace(section)));

        return AnswerCoachingValidator.IsGroundedReportEvidence(evidence, illustrativeText);
    }

    public override string BuildRepairInstructions(AiValidationResult<AnswerEvaluation> priorResult, string originalInstructions)
    {
        if (string.Equals(priorResult.FailureReason, "interview.rubric_evidence_ungrounded", StringComparison.Ordinal))
        {
            return $"""
                {originalInstructions}

                IMPORTANT CANDIDATE-EVIDENCE CORRECTION:
                The prior rubric evidence was not grounded in the ORIGINAL submitted candidate answer. Rewrite every rubric evidence field using only a direct phrase or fact from that answer. The question, role/context, rubric, and hypothetical sampleAnswer are never candidate evidence. Do not copy any sample project, technology, responsibility, result, or metric into rubric evidence. Keep the four canonical criteria and score honestly; where the answer lacks detail, cite only what it actually says and score below 60 as appropriate.
                Return a completely corrected object matching the schema. Preserve the separate sampleAnswer only as clearly illustrative teaching content.
                """;
        }

        if (string.Equals(priorResult.FailureReason, "star.evidence_ungrounded", StringComparison.Ordinal))
        {
            return $"""
                {originalInstructions}

                IMPORTANT STAR-EVIDENCE CORRECTION:
                The prior STAR evidence was not grounded in the ORIGINAL submitted candidate answer. Re-evaluate the full answer and use only direct phrases/facts from it as STAR evidence; the question, context, and hypothetical sampleAnswer are not candidate evidence. Never copy sample-only projects, technologies, responsibilities, outcomes, or metrics into STAR evidence. For a component without direct candidate evidence, set detected=false, score=0, and evidence to an empty string.
                Return a completely corrected object matching the schema and keep any sampleAnswer clearly separate and illustrative.
                """;
        }

        if (string.Equals(priorResult.FailureReason, "rubric.criteria_missing", StringComparison.Ordinal))
        {
            return $"""
                {originalInstructions}

                IMPORTANT RUBRIC CORRECTION INSTRUCTION:
                The previous structured evaluation failed validation with reason 'rubric.criteria_missing' because one or more required rubric criteria were missing.
                Return exactly four rubric items: one each for correctness, structure, completeness, and clarity.
                Include every criterion exactly once; there must be no duplicate or missing criteria.
                Each item must have an integer score from 0 to 100 and non-blank evidence grounded in the ORIGINAL candidate answer.
                Return a completely corrected object matching the schema.
                """;
        }

        if (priorResult.FailureReason is "interview.strengths_ungrounded" or "interview.strengths_blank")
        {
            return $"""
                {originalInstructions}

                IMPORTANT COACHING CORRECTION INSTRUCTION:
                The previous structured evaluation failed validation because its strengths were not grounded in the ORIGINAL candidate answer: '{priorResult.FailureReason}'
                Rewrite strengths using only concrete words, technologies, actions, facts, or results explicitly present in the ORIGINAL candidate answer. The question, context, rubric scores, and rubric evidence may guide relevance or scoring but must not be used as evidence of what the candidate stated or did. Remove inferred traits such as leadership, ownership, production experience, business impact, mentoring, scale, team size, architecture ownership, deployment success, or measurable outcomes unless directly stated. Do not add new facts, technologies, responsibilities, achievements, or results.
                If no grounded positive evidence is present, return "strengths": [] (an empty strengths collection), use rubric scores below 60 where justified, and do not invent a strength. Otherwise return 1-3 modest grounded strengths.
                Preserve every other already-valid field where possible. In all cases, return 1-3 concrete actionable improvements and a faithful improvedAnswer. Do not invent facts, experience, technologies, achievements, or metrics.
                Return a completely corrected object matching the schema.
                """;
        }

        if (string.Equals(priorResult.FailureReason, "interview.improvements_invalid", StringComparison.Ordinal))
        {
            return $"""
                {originalInstructions}

                IMPORTANT IMPROVEMENTS CORRECTION INSTRUCTION:
                The previous structured evaluation failed validation with reason 'interview.improvements_invalid'.
                Return improvements as a JSON array containing 1 to 3 unique, non-empty strings, each no longer than 500 characters. Each item must be substantive coaching that tells the candidate a concrete action to take. Whitespace-only items and duplicate or equivalent items are not valid separate improvements.
                Improvements may tell the candidate to add missing evidence, clarify structure, quantify a result they actually stated, or use a clearer explanation. They must not assert or fabricate technologies, projects, responsibilities, metrics, team size, production claims, or experience absent from the ORIGINAL candidate answer.
                Preserve every other already-valid field where possible and return a completely corrected object matching the schema.
                """;
        }

        if (string.Equals(priorResult.FailureReason, "interview.improvements_not_actionable", StringComparison.Ordinal))
        {
            return $"""
                {originalInstructions}

                IMPORTANT COACHING CORRECTION INSTRUCTION:
                The previous structured evaluation failed validation with reason 'interview.improvements_not_actionable' because it contained a non-actionable improvement.
                Return 1-3 improvement items. Every item must tell the candidate a direct action to take and must start with or clearly include a substantive action verb, such as add, include, explain, quantify, clarify, describe, mention, specify, show, provide, use, connect, highlight, focus, outline, state, compare, give, identify, emphasize, present, nêu, bổ sung, thêm, định lượng, làm rõ, giải thích, or mô tả. Directive prefixes such as 'hãy', 'nên', or 'có thể' may introduce an action, but do not count by themselves. Reject passive observations such as 'the result is unclear' or 'phần kết quả hơi yếu'.
                Keep every improvement grounded in the ORIGINAL candidate answer and preserve every other already-valid field where possible. Do not invent facts, experience, or technologies.
                Return a completely corrected object matching the schema.
                """;
        }

        if (priorResult.FailureReason is "interview.improved_answer_ungrounded" or "interview.improved_answer_fabricated")
        {
            return $"""
                {originalInstructions}

                IMPORTANT IMPROVED-ANSWER CORRECTION INSTRUCTION:
                The previous structured evaluation failed validation with reason '{priorResult.FailureReason}'.
                Preserve all already-valid rubric scores, rubric evidence, feedback, STAR, strengths, and improvements exactly. ONLY rewrite improvedAnswer; do not revise any other field.
                Rewrite improvedAnswer using ONLY facts explicitly present in the ORIGINAL candidate answer. This means using only facts, technologies, responsibilities, actions, and outcomes explicitly present there. Reordering, rephrasing, and clearer structure are allowed. The question, rubric, Job Description, resume context, Career Goal, system instructions, and interviewer context are not candidate facts.
                Do not introduce new technologies, projects, responsibilities, metrics, team size, production claims, roles, achievements, impact, or inferred experience. Placeholders are allowed for missing facts; use a concise placeholder asking the candidate to add the evidence rather than fabricating it. If no richer grounded rewrite is possible, preserve the candidate's original answer instead of inventing details.
                Do not revise any other field, including grounded strengths and actionable improvements.
                Return a completely corrected object matching the schema.
                """;
        }

        if (priorResult.FailureReason?.StartsWith("star.", StringComparison.Ordinal) == true)
        {
            return $"""
                {originalInstructions}

                IMPORTANT STAR CORRECTION INSTRUCTION:
                The previous structured evaluation violated the STAR contract: '{priorResult.FailureReason}'.
                Re-evaluate the ORIGINAL candidate answer.
                Important:
                - The question focus does not restrict STAR extraction; inspect the entire candidate answer.
                - Extract evidence before assigning detected/score.
                - Concrete technical actions (e.g. profiling, queries, coding, caching, indexing, coordinating) are Action.
                - Measurable operational outcomes (e.g. latency, recovery, runbook, metrics) are Result.
                - detected=false requires score=0 and empty evidence.
                - detected=true requires score 1-100 and direct evidence quote.
                Return a completely corrected object matching the schema.
                """;
        }

        if (priorResult.FailureReason?.StartsWith("interview.", StringComparison.Ordinal) == true)
        {
            return $"""
                {originalInstructions}

                IMPORTANT COACHING CORRECTION INSTRUCTION:
                The previous structured evaluation violated the per-answer coaching contract: '{priorResult.FailureReason}'.
                Return 1-3 nonblank strengths grounded in the original candidate answer, 1-3 concrete actionable improvements, and one nonblank improvedAnswer.
                Do not add metrics, achievements, technologies, roles, or experience that the candidate did not provide. If evidence is missing, ask the candidate to add it instead of inventing it.
                Return a completely corrected object matching the schema.
                """;
        }

        return base.BuildRepairInstructions(priorResult, originalInstructions);
    }

    public override AiValidationResult<AnswerEvaluation>? TryRecoverTerminalValidation(
        AnswerEvaluation? raw,
        AiOperationContext context,
        AiValidationResult<AnswerEvaluation> terminalResult)
    {
        if (raw is null || string.IsNullOrWhiteSpace(context.CandidateAnswer))
            return null;

        if (terminalResult.FailureReason is "interview.strengths_ungrounded")
        {
            var answer = context.CandidateAnswer;
            var strengths = (raw.Strengths ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item) &&
                    AnswerCoachingValidator.IsGroundedReportStrength(item, answer))
                .ToArray();
            if (strengths.Length == 0 && raw.Scores is not null && raw.Scores.Any(score => score is not null && score.Score >= 60))
            {
                strengths = raw.Scores
                    .Where(score => score is not null && score.Score >= 60 &&
                        AnswerCoachingValidator.IsGroundedReportStrength(score.Evidence, answer))
                    .Select(score => score.Evidence.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToArray();
            }

            return NormalizeAndValidate(raw with { Strengths = strengths }, context);
        }

        if (terminalResult.FailureReason is not ("interview.improved_answer_ungrounded" or "interview.improved_answer_fabricated"))
            return null;

        var candidateAnswer = context.CandidateAnswer.Trim();
        var groundedFallback = candidateAnswer.Length <= 4_000
            ? candidateAnswer
            : candidateAnswer[..4_000].TrimEnd();

        return NormalizeAndValidate(raw with { ImprovedAnswer = groundedFallback }, context);
    }

}
