using Nexora.Business.Career;
using Nexora.Business.Common;

namespace Nexora.UnitTests.Career;

public sealed class CareerGoalRulesTests
{
    [Theory]
    [InlineData("junior", "junior")]
    [InlineData(" Senior ", "senior")]
    [InlineData("entry-level", "entry")]
    [InlineData("mid-level", "mid")]
    public void SeniorityIsNormalizedToAStableCanonicalValue(string input, string expected)
    {
        Assert.Equal(expected, CareerGoalRules.NormalizeSeniority(input));
    }

    [Fact]
    public void UnknownSeniorityUsesTheCanonicalValidationError()
    {
        var exception = Assert.Throws<BusinessException>(() => CareerGoalRules.NormalizeSeniority("wizard"));

        Assert.Equal("INVALID_SENIORITY", exception.Code);
        Assert.Equal(BusinessErrorKind.Validation, exception.Kind);
    }

    [Fact]
    public void OptionalWhitespaceIsNormalizedToNull()
    {
        Assert.Null(CareerGoalRules.NormalizeOptional("  ", CareerGoalRules.IndustryMaxLength, "Industry"));
    }
}
