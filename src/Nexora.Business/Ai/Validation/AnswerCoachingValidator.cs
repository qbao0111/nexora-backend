using Nexora.Business.Practice;

namespace Nexora.Business.Ai;

public static class AnswerCoachingValidator
{
    private const int NoPositiveEvidenceScoreThreshold = 60;
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "how", "in", "is", "it", "of", "on", "or", "that", "the", "to", "was", "with",
        "bạn", "có", "của", "cho", "đã", "được", "là", "một", "này", "nêu", "và", "với", "trong", "cần", "nên"
    };

    private static readonly string[] UnsupportedFactTerms =
    [
        "kubernetes", "docker", "postgresql", "mysql", "mongodb", "redis", "kafka", "graphql", "react", "python", "java", "golang", "typescript", "javascript", "rabbitmq", "c#", ".net", "asp.net", "aws", "azure", "gcp", "sql",
        "led", "leadership", "pressure", "mentor", "mentored", "mentoring", "managed", "owned", "achieved", "built", "implemented", "designed", "deployed", "launched", "migrated", "migration", "certified", "experience", "expertise", "experienced", "team", "project", "production", "incident", "business impact", "team size", "architecture ownership", "large-scale", "kinh nghiệm", "triển khai", "xây dựng", "dẫn dắt", "lãnh đạo", "đội nhóm", "quản lý", "sở hữu", "vận hành", "quy mô", "tác động kinh doanh"
    ];

    private static readonly string[] SubstantiveActionMarkers =
    [
        "add", "include", "explain", "quantify", "clarify", "describe", "mention", "specify", "show", "provide", "use", "connect", "highlight", "focus", "outline", "state", "compare", "give", "identify", "emphasize", "present",
        "tập trung", "trình bày", "làm nổi bật", "liên hệ", "đưa ví dụ", "đưa thêm", "chỉ ra", "nhấn mạnh", "so sánh", "giải thích", "mô tả", "làm rõ", "bổ sung", "nêu", "định lượng", "cụ thể hóa", "đưa ra", "thêm"
    ];

    private static readonly string[] SafeImprovedAnswerMarkers =
    [
        "keep the same answer", "giữ nguyên câu trả lời"
    ];

    private static readonly HashSet<string> GenericStrengthTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "answer", "response", "candidate", "question", "clear", "concise", "specific", "relevant", "grounded", "strong", "good", "well",
        "organized", "structured", "technical", "explanation", "explain", "explains", "debug", "debugging", "analysis", "analytical",
        "example", "result", "impact", "evidence", "concrete", "available", "overall", "same", "keep", "add", "include", "quantify", "clarify",
        "describe", "mention", "specify", "show", "provide", "use", "can", "could", "should", "would", "if", "you", "your", "point", "points",
        "structure", "approach", "logic", "reasoning", "process", "method", "detail", "details", "context", "complete", "accurate", "focused",
        "direct", "thoughtful", "thorough", "coherent", "understanding", "knowledge", "solution", "problem", "solving", "communication", "communicates",
        "nêu", "bổ sung", "thêm", "định lượng", "làm rõ", "giải thích", "mô tả", "đưa ra", "cụ thể", "ví dụ", "kết quả", "tác động", "bằng chứng",
        "giữ", "nguyên", "câu", "trả", "lời", "phần", "nếu", "có", "rõ", "việc", "công", "nghệ", "backend"
    };

    private static readonly Dictionary<string, string[]> UnsupportedFactVariants =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["xây dựng"] = ["build", "built", "create", "created", "implement", "implemented"],
            ["triển khai"] = ["deploy", "deployed", "deployment"],
            ["dẫn dắt"] = ["lead", "led", "leadership"],
            ["lãnh đạo"] = ["lead", "led", "leadership"],
            ["đội nhóm"] = ["team"],
            ["quản lý"] = ["manage", "managed", "management"],
            ["sở hữu"] = ["own", "owned", "ownership"],
            ["vận hành"] = ["operate", "operated", "operations", "production"],
            ["quy mô"] = ["scale", "scalable", "large-scale"],
            ["tác động kinh doanh"] = ["business impact"],
            ["built"] = ["build", "xây dựng", "xây"],
            ["implemented"] = ["implement", "xây dựng", "triển khai"],
            ["designed"] = ["design", "thiết kế"],
            ["deployed"] = ["deploy", "triển khai"],
            ["led"] = ["lead", "leadership", "dẫn dắt", "lãnh đạo"],
            ["managed"] = ["manage", "management", "quản lý"],
            ["owned"] = ["own", "ownership", "sở hữu"],
            ["achieved"] = ["achieve", "achievement", "đạt được"],
            ["experience"] = ["kinh nghiệm", "experienced"],
            ["expertise"] = ["chuyên sâu", "expert", "thành thạo"],
            ["experienced"] = ["experience", "kinh nghiệm"],
            ["team"] = ["đội nhóm", "team"],
            ["production"] = ["vận hành", "production"],
            ["incident"] = ["sự cố", "incident"]
        };

    private static readonly HashSet<string> CandidateSpecificFactTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "lead", "led", "leadership", "mentor", "mentored", "mentoring", "managed", "management", "owned", "ownership", "achieved", "achievement",
        "built", "implemented", "designed", "deployed", "launched", "migrated", "migration", "certified", "experience", "team", "project",
        "production", "incident", "revenue", "customer", "client", "users", "role", "responsibility", "promotion", "award", "degree", "certificate"
    };

    private static readonly HashSet<string> CommonCapitalizedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "api", "answer", "candidate", "clear", "do", "good", "i", "if", "in", "it", "keep", "no", "one", "or", "overall",
        "please", "question", "return", "strong", "the", "this", "to", "use", "using", "we", "when", "with", "you"
    };

    private static readonly string[] TechnologyIdentifierSuffixes =
    [
        "api", "cache", "cloud", "db", "flow", "hub", "js", "ml", "mq", "net", "queue", "search", "script", "sdk", "sql", "stack", "ts", "ware"
    ];

    private static readonly HashSet<string> TechnologyContextMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "backed", "built", "created", "deployed", "implemented", "integrated", "migrated", "powered",
        "use", "used", "using", "via", "integrate", "deploy", "build", "create", "implement", "dùng"
    };

    private static readonly string[] TechnologyContextPhrases =
    [
        "sử dụng", "đã dùng", "tích hợp", "triển khai"
    ];

    private static readonly HashSet<string> TechnicalAnchorTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "api", "asp", "backend", "cache", "cloud", "code", "database", "databases", "db", "endpoint", "framework",
        "library", "libraries", "latency", "net", "platform", "production", "queue", "search", "server", "service",
        "services", "software", "sql", "system", "technology", "technical"
    };

    private static readonly HashSet<string> GenericCoachingTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "answer", "response", "candidate", "question", "clear", "concise", "specific", "relevant", "grounded", "strong", "good", "well",
        "organized", "structured", "technical", "explanation", "explain", "example", "result", "impact", "evidence", "concrete", "available",
        "overall", "same", "keep", "add", "include", "quantify", "clarify", "describe", "mention", "specify", "show", "provide", "use",
        "can", "could", "should", "would", "if", "you", "your", "nêu", "bổ sung", "thêm", "định lượng", "làm rõ", "giải thích", "mô tả",
        "đưa ra", "cụ thể", "ví dụ", "kết quả", "tác động", "bằng chứng", "giữ", "nguyên", "câu", "trả", "lời", "nếu", "có"
    };

    public static AiValidationResult<AnswerCoachingOutput> ValidateAndNormalize(
        IReadOnlyCollection<string>? rawStrengths,
        IReadOnlyCollection<string>? rawImprovements,
        string? rawImprovedAnswer,
        string? candidateAnswer,
        IReadOnlyCollection<RubricScore> rubricScores)
    {
        // An empty strengths collection is an explicit absence signal only when
        // every rubric score is below the existing 60-point coaching threshold.
        // Non-empty strengths continue through the grounding checks below.
        var allowEmptyStrengths = rubricScores.Count > 0 &&
            rubricScores.All(score => score.Score < NoPositiveEvidenceScoreThreshold);
        var strengths = NormalizeList(rawStrengths, "strengths", allowEmptyStrengths, out var strengthsFailure);
        if (strengths is null)
            return AiValidationResult<AnswerCoachingOutput>.Failure(strengthsFailure!, "semantic", repairable: true);

        var improvements = NormalizeList(rawImprovements, "improvements", allowEmpty: false, out var improvementsFailure);
        if (improvements is null)
            return AiValidationResult<AnswerCoachingOutput>.Failure(improvementsFailure!, "semantic", repairable: true);

        if (improvements.Any(item => !ContainsActionMarker(item)))
            return AiValidationResult<AnswerCoachingOutput>.Failure("interview.improvements_not_actionable", "semantic", repairable: true);

        if (string.IsNullOrWhiteSpace(rawImprovedAnswer))
            return AiValidationResult<AnswerCoachingOutput>.Failure("interview.improved_answer_blank", "semantic", repairable: true);

        var improvedAnswer = rawImprovedAnswer.Trim();
        if (improvedAnswer.Length > 4_000)
            return AiValidationResult<AnswerCoachingOutput>.Failure("interview.improved_answer_too_long", "semantic", repairable: true);

        if (!string.IsNullOrWhiteSpace(candidateAnswer))
        {
            var answer = candidateAnswer.Trim();
            if (strengths.Any(strength =>
                ContainsUnsupportedFact(strength, answer) ||
                ContainsNovelStrengthClaim(strength, answer, allowGenericParaphrases: true) ||
                !HasMeaningfulOverlap(strength, answer)))
                return AiValidationResult<AnswerCoachingOutput>.Failure("interview.strengths_ungrounded", "semantic", repairable: true);

            var preservesExactAnswer = string.Equals(improvedAnswer, answer, StringComparison.Ordinal);
            if (!preservesExactAnswer && !HasMeaningfulOverlap(improvedAnswer, answer) && !IsSafePlaceholder(improvedAnswer))
                return AiValidationResult<AnswerCoachingOutput>.Failure("interview.improved_answer_ungrounded", "semantic", repairable: true);

            if (ContainsUnsupportedFact(improvedAnswer, answer) || ContainsNovelCandidateFact(improvedAnswer, answer))
                return AiValidationResult<AnswerCoachingOutput>.Failure("interview.improved_answer_fabricated", "semantic", repairable: true);
        }

        return AiValidationResult<AnswerCoachingOutput>.Success(new AnswerCoachingOutput(strengths, improvements, improvedAnswer));
    }

    public static bool IsGroundedReportEvidence(string output, string groundingTranscript) =>
        HasGroundingOverlap(output, groundingTranscript) &&
        !ContainsUnsupportedFact(output, groundingTranscript) &&
        !ContainsNovelCandidateFact(output, groundingTranscript);

    public static bool IsGroundedReportStrength(string output, string groundingTranscript) =>
        HasGroundingOverlap(output, groundingTranscript) &&
        !ContainsUnsupportedFact(output, groundingTranscript) &&
        !ContainsNovelStrengthClaim(output, groundingTranscript);

    private static bool HasGroundingOverlap(string output, string groundingTranscript) =>
        !string.IsNullOrWhiteSpace(groundingTranscript) &&
        (!string.IsNullOrWhiteSpace(output) && groundingTranscript.Contains(output.Trim(), StringComparison.OrdinalIgnoreCase) ||
         HasMeaningfulOverlap(output, groundingTranscript));

    private static string[]? NormalizeList(
        IReadOnlyCollection<string>? values,
        string name,
        bool allowEmpty,
        out string? failure)
    {
        failure = null;
        if (values is null)
        {
            failure = $"interview.{name}_invalid";
            return null;
        }

        var normalized = values
            .Select(value => value?.Trim() ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Any(string.IsNullOrWhiteSpace))
        {
            failure = $"interview.{name}_blank";
            return null;
        }

        if (normalized.Any(value => value.Length > 500))
        {
            failure = $"interview.{name}_too_long";
            return null;
        }

        if (normalized.Length > 3 || (!allowEmpty && normalized.Length < 1))
        {
            failure = $"interview.{name}_invalid";
            return null;
        }

        return normalized;
    }

    private static bool HasMeaningfulOverlap(string output, string answer)
    {
        var answerTokens = Tokens(answer).Where(IsMeaningful).ToArray();
        return Tokens(output).Where(IsMeaningful).Any(outputToken =>
            answerTokens.Any(answerToken =>
                string.Equals(outputToken, answerToken, StringComparison.OrdinalIgnoreCase) ||
                (outputToken.Length >= 5 && answerToken.StartsWith(outputToken, StringComparison.OrdinalIgnoreCase)) ||
                (answerToken.Length >= 5 && outputToken.StartsWith(answerToken, StringComparison.OrdinalIgnoreCase))));
    }

    private static bool ContainsActionMarker(string value)
    {
        var normalized = NormalizeForMatching(value);
        return SubstantiveActionMarkers.Any(marker =>
        {
            var normalizedMarker = NormalizeForMatching(marker);
            if (normalizedMarker.Contains(' ', StringComparison.Ordinal))
                return normalized.Contains(normalizedMarker, StringComparison.Ordinal);

            return System.Text.RegularExpressions.Regex.IsMatch(
                normalized,
                $@"(?<![\p{{L}}\p{{N}}]){System.Text.RegularExpressions.Regex.Escape(normalizedMarker)}(?![\p{{L}}\p{{N}}])",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        });
    }

    private static bool ContainsNovelCandidateFact(string output, string answer)
    {
        if (ContainsNovelConcreteIdentifier(output, answer))
            return true;

        var answerTokens = Tokens(answer).Where(IsMeaningful).ToArray();
        return Tokens(output)
            .Where(IsMeaningful)
            .Where(token => !GenericCoachingTokens.Contains(token))
            .Where(token => !answerTokens.Any(answerToken => TokensMatch(token, answerToken)))
            .Any(token => CandidateSpecificFactTokens.Contains(token, StringComparer.OrdinalIgnoreCase));
    }

    private static bool ContainsNovelConcreteIdentifier(string output, string answer)
    {
        var answerIdentifiers = ExtractCapitalizedIdentifiers(answer)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var answerTokens = Tokens(answer).Where(IsMeaningful).ToArray();

        if (ExtractCapitalizedIdentifierMatches(output)
            .Where(identifier => !CommonCapitalizedWords.Contains(identifier.Value))
            .Where(identifier => !IsIdentifierSupported(identifier.Value, answerIdentifiers, answerTokens))
            .Any(identifier => IsOpenVocabularyConcreteIdentifier(output, identifier)))
            return true;

        var outputTokens = Tokens(output).ToArray();
        return outputTokens
            .Select((token, index) => (token, index))
            .Where(item => IsMeaningful(item.token))
            .Where(item => !answerTokens.Any(answerToken => TokensMatch(item.token, answerToken)))
            .Any(item => IsTechnologyLikeIdentifier(item.token) ||
                         IsNovelLowercaseTechnologyReference(output, outputTokens, item.index));
    }

    private static bool IsNovelLowercaseTechnologyReference(
        string output,
        string[] outputTokens,
        int identifierIndex)
    {
        var identifier = outputTokens[identifierIndex];
        if (GenericStrengthTokens.Contains(identifier))
            return false;

        // Lowercase open-vocabulary references need both a direct technology-use
        // context and another technical anchor so ordinary prose is not treated
        // as a concrete candidate fact merely because it follows "used".
        return HasTechnologyUseContext(outputTokens, identifierIndex) &&
            HasTechnicalAnchor(output, outputTokens, identifierIndex);
    }

    private static bool HasTechnologyUseContext(string[] outputTokens, int identifierIndex)
    {
        if (identifierIndex > 0 && TechnologyContextMarkers.Contains(outputTokens[identifierIndex - 1]))
            return true;

        return identifierIndex > 1 &&
            ((outputTokens[identifierIndex - 2], outputTokens[identifierIndex - 1]) is ("sử", "dụng") or
             ("tích", "hợp") or
             ("triển", "khai"));
    }

    private static bool HasTechnicalAnchor(
        string output,
        string[] outputTokens,
        int identifierIndex)
    {
        if (outputTokens
            .Select((token, index) => (token, index))
            .Where(item => item.index != identifierIndex)
            .Any(item => TechnicalAnchorTokens.Contains(item.token) || IsTechnologyLikeIdentifier(item.token)))
            return true;

        return ExtractCapitalizedIdentifierMatches(output)
            .Any(identifier => IsTechnologyLikeIdentifier(identifier.Value));
    }

    private static bool IsIdentifierSupported(
        string identifier,
        HashSet<string> answerIdentifiers,
        IReadOnlyCollection<string> answerTokens) =>
        answerIdentifiers.Contains(identifier) ||
        Tokens(identifier).Any(identifierToken =>
            answerTokens.Any(answerToken => TokensMatch(identifierToken, answerToken)));

    private static bool IsOpenVocabularyConcreteIdentifier(
        string output,
        (string Value, int Index) identifier) =>
        IsTechnologyLikeIdentifier(identifier.Value) ||
        HasOpenVocabularyIdentifierContext(output, identifier.Index);

    private static bool IsTechnologyLikeIdentifier(string identifier)
    {
        if (identifier.Skip(1).Any(char.IsUpper))
            return true;

        var normalized = identifier.ToLowerInvariant();
        return TechnologyIdentifierSuffixes.Any(suffix =>
            normalized.Length > suffix.Length + 1 &&
            normalized.EndsWith(suffix, StringComparison.Ordinal));
    }

    private static IEnumerable<string> ExtractCapitalizedIdentifiers(string value)
        => ExtractCapitalizedIdentifierMatches(value).Select(match => match.Value);

    private static IEnumerable<(string Value, int Index)> ExtractCapitalizedIdentifierMatches(string value)
    {
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     value,
                     @"(?<![\p{L}\p{N}])(?:[A-Z][a-z]{2,}[A-Za-z0-9]*|[A-Z]{2,}[A-Za-z0-9+#.-]*)(?![\p{L}\p{N}])"))
        {
            yield return (match.Value, match.Index);
        }
    }

    private static bool HasOpenVocabularyIdentifierContext(string output, int identifierIndex)
    {
        var prefix = output[..identifierIndex];
        var previousToken = Tokens(prefix).LastOrDefault();
        if (previousToken is not null && TechnologyContextMarkers.Contains(previousToken))
            return true;

        var normalizedPrefix = NormalizeForMatching(prefix);
        return TechnologyContextPhrases.Any(phrase =>
            normalizedPrefix.EndsWith(phrase, StringComparison.Ordinal));
    }

    private static bool ContainsNovelStrengthClaim(
        string output,
        string answer,
        bool allowGenericParaphrases = false)
    {
        if (ContainsNovelConcreteIdentifier(output, answer))
            return true;

        var answerTokens = Tokens(answer).Where(IsMeaningful).ToArray();
        var unmatchedTokens = Tokens(output)
            .Where(IsMeaningful)
            .Where(token => !GenericStrengthTokens.Contains(token))
            .Where(token => !answerTokens.Any(answerToken => TokensMatch(token, answerToken)))
            .ToArray();

        return allowGenericParaphrases
            ? unmatchedTokens.Any(token => CandidateSpecificFactTokens.Contains(token, StringComparer.OrdinalIgnoreCase))
            : unmatchedTokens.Length > 0;
    }

    private static bool TokensMatch(string outputToken, string answerToken) =>
        string.Equals(outputToken, answerToken, StringComparison.OrdinalIgnoreCase) ||
        (outputToken.Length >= 5 && answerToken.StartsWith(outputToken, StringComparison.OrdinalIgnoreCase)) ||
        (answerToken.Length >= 5 && outputToken.StartsWith(answerToken, StringComparison.OrdinalIgnoreCase));

    private static bool IsSafePlaceholder(string value) =>
        SafeImprovedAnswerMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsUnsupportedFact(string output, string answer)
    {
        var answerNumbers = System.Text.RegularExpressions.Regex.Matches(answer, @"\d+(?:[.,]\d+)?")
            .Select(match => match.Value.Replace(",", ".", StringComparison.Ordinal))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outputNumbers = System.Text.RegularExpressions.Regex.Matches(output, @"\d+(?:[.,]\d+)?")
            .Select(match => match.Value.Replace(",", ".", StringComparison.Ordinal));
        if (outputNumbers.Any(number => !answerNumbers.Contains(number)))
            return true;

        var normalizedOutput = NormalizeForMatching(output);
        var normalizedAnswer = NormalizeForMatching(answer);
        return UnsupportedFactTerms.Any(term =>
            normalizedOutput.Contains(NormalizeForMatching(term), StringComparison.Ordinal) &&
            !IsFactTermSupported(term, normalizedAnswer));
    }

    private static bool IsFactTermSupported(string term, string answer)
    {
        if (answer.Contains(NormalizeForMatching(term), StringComparison.Ordinal))
            return true;

        return UnsupportedFactVariants.TryGetValue(term, out var variants) &&
            variants.Any(variant => answer.Contains(NormalizeForMatching(variant), StringComparison.Ordinal));
    }

    private static IEnumerable<string> Tokens(string value) =>
        System.Text.RegularExpressions.Regex.Matches(NormalizeForMatching(value), @"[\p{L}\p{N}]+(?:[+#.]*)")
            .Select(match => match.Value.Trim('.', '+'));

    private static string NormalizeForMatching(string value)
    {
        var normalized = value.Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.Replace(
            normalized,
            @"\s+",
            " ",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant).Trim();
    }

    private static bool IsMeaningful(string token) =>
        token.Length >= 3 && !StopWords.Contains(token);
}
