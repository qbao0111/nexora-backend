using Nexora.Business.Ai;
using Nexora.Business.Practice;

namespace Nexora.UnitTests.Ai;

public sealed class CanonicalRubricValidatorTests
{
    [Fact]
    public void ValidateAndNormalizeAcceptsExactCanonicalCriteriaAndNormalizes()
    {
        var input = new[]
        {
            new RubricScore(" Correctness ", 85, "Good answer"),
            new RubricScore("STRUCTURE", 75, "Logical flow"),
            new RubricScore("Completeness", 90, "Covered everything"),
            new RubricScore(" clarity ", 80, "Very clear")
        };

        var result = CanonicalRubricValidator.ValidateAndNormalize(input);

        Assert.True(result.IsValid);
        Assert.NotNull(result.NormalizedValue);
        Assert.Equal(4, result.NormalizedValue.Count);
        Assert.Contains(result.NormalizedValue, s => s.Criterion == "correctness" && s.Score == 85);
        Assert.Contains(result.NormalizedValue, s => s.Criterion == "structure" && s.Score == 75);
        Assert.Contains(result.NormalizedValue, s => s.Criterion == "completeness" && s.Score == 90);
        Assert.Contains(result.NormalizedValue, s => s.Criterion == "clarity" && s.Score == 80);
    }

    [Fact]
    public void ValidateAndNormalizeRejectsMissingCriterion()
    {
        var input = new[]
        {
            new RubricScore("correctness", 85, "Good answer"),
            new RubricScore("structure", 75, "Logical flow"),
            new RubricScore("clarity", 80, "Very clear")
        };

        var result = CanonicalRubricValidator.ValidateAndNormalize(input);

        Assert.False(result.IsValid);
        Assert.Equal("rubric.criteria_missing", result.FailureReason);
        Assert.True(result.Repairable);
    }

    [Fact]
    public void ValidateAndNormalizeRejectsExtraCriterion()
    {
        var input = new[]
        {
            new RubricScore("correctness", 85, "Good answer"),
            new RubricScore("structure", 75, "Logical flow"),
            new RubricScore("completeness", 90, "Covered everything"),
            new RubricScore("clarity", 80, "Very clear"),
            new RubricScore("confidence", 70, "Extra criterion")
        };

        var result = CanonicalRubricValidator.ValidateAndNormalize(input);

        Assert.False(result.IsValid);
        Assert.Equal("rubric.criteria_extra", result.FailureReason);
        Assert.True(result.Repairable);
    }

    [Fact]
    public void ValidateAndNormalizeRejectsDuplicateCriterion()
    {
        var input = new[]
        {
            new RubricScore("correctness", 85, "Good answer"),
            new RubricScore("correctness", 75, "Duplicate"),
            new RubricScore("completeness", 90, "Covered everything"),
            new RubricScore("clarity", 80, "Very clear")
        };

        var result = CanonicalRubricValidator.ValidateAndNormalize(input);

        Assert.False(result.IsValid);
        Assert.Equal("rubric.criteria_duplicate", result.FailureReason);
        Assert.True(result.Repairable);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(150)]
    public void ValidateAndNormalizeRejectsScoreOutOfRange(int invalidScore)
    {
        var input = new[]
        {
            new RubricScore("correctness", invalidScore, "Good answer"),
            new RubricScore("structure", 75, "Logical flow"),
            new RubricScore("completeness", 90, "Covered everything"),
            new RubricScore("clarity", 80, "Very clear")
        };

        var result = CanonicalRubricValidator.ValidateAndNormalize(input);

        Assert.False(result.IsValid);
        Assert.Equal("rubric.score_out_of_range", result.FailureReason);
        Assert.True(result.Repairable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateAndNormalizeRejectsBlankEvidence(string? blankEvidence)
    {
        var input = new[]
        {
            new RubricScore("correctness", 85, blankEvidence!),
            new RubricScore("structure", 75, "Logical flow"),
            new RubricScore("completeness", 90, "Covered everything"),
            new RubricScore("clarity", 80, "Very clear")
        };

        var result = CanonicalRubricValidator.ValidateAndNormalize(input);

        Assert.False(result.IsValid);
        Assert.Equal("rubric.evidence_blank", result.FailureReason);
        Assert.True(result.Repairable);
    }

    [Fact]
    public void ValidateAndNormalizeTreatsNullRubricItemAsRepairableSchemaFailure()
    {
        var input = new RubricScore[]
        {
            null!,
            new RubricScore("structure", 75, "Logical flow"),
            new RubricScore("completeness", 90, "Covered everything"),
            new RubricScore("clarity", 80, "Very clear")
        };

        var result = CanonicalRubricValidator.ValidateAndNormalize(input);

        Assert.False(result.IsValid);
        Assert.Equal("rubric.item_invalid", result.FailureReason);
        Assert.Equal("semantic", result.ValidationStage);
        Assert.True(result.Repairable);
    }
}
