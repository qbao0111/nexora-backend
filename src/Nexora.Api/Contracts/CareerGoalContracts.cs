using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Nexora.Business.Career;

namespace Nexora.Api.Contracts;

public sealed class CreateCareerGoalRequest
{
    [Required, StringLength(CareerGoalRules.TargetRoleMaxLength, MinimumLength = 1)]
    public string TargetRole { get; init; } = string.Empty;

    [Required, StringLength(CareerGoalRules.SeniorityMaxLength, MinimumLength = 1)]
    public string Seniority { get; init; } = string.Empty;

    [StringLength(CareerGoalRules.IndustryMaxLength)]
    public string? Industry { get; init; }

    [StringLength(CareerGoalRules.TargetCompanyMaxLength)]
    public string? TargetCompany { get; init; }

    public Guid? TargetJobDescriptionId { get; init; }
    public DateOnly? TargetDate { get; init; }
}

public sealed class UpdateCareerGoalRequest
{
    private string? targetRole;
    private string? seniority;
    private string? industry;
    private string? targetCompany;
    private Guid? targetJobDescriptionId;
    private DateOnly? targetDate;
    private bool? active;

    [StringLength(CareerGoalRules.TargetRoleMaxLength, MinimumLength = 1)]
    public string? TargetRole
    {
        get => targetRole;
        init
        {
            targetRole = value;
            TargetRoleSpecified = true;
        }
    }

    [StringLength(CareerGoalRules.SeniorityMaxLength, MinimumLength = 1)]
    public string? Seniority
    {
        get => seniority;
        init
        {
            seniority = value;
            SenioritySpecified = true;
        }
    }

    [StringLength(CareerGoalRules.IndustryMaxLength)]
    public string? Industry
    {
        get => industry;
        init
        {
            industry = value;
            IndustrySpecified = true;
        }
    }

    [StringLength(CareerGoalRules.TargetCompanyMaxLength)]
    public string? TargetCompany
    {
        get => targetCompany;
        init
        {
            targetCompany = value;
            TargetCompanySpecified = true;
        }
    }

    public Guid? TargetJobDescriptionId
    {
        get => targetJobDescriptionId;
        init
        {
            targetJobDescriptionId = value;
            TargetJobDescriptionIdSpecified = true;
        }
    }

    public DateOnly? TargetDate
    {
        get => targetDate;
        init
        {
            targetDate = value;
            TargetDateSpecified = true;
        }
    }

    public bool? Active
    {
        get => active;
        init
        {
            active = value;
            ActiveSpecified = true;
        }
    }

    [JsonIgnore] public bool TargetRoleSpecified { get; private set; }
    [JsonIgnore] public bool SenioritySpecified { get; private set; }
    [JsonIgnore] public bool IndustrySpecified { get; private set; }
    [JsonIgnore] public bool TargetCompanySpecified { get; private set; }
    [JsonIgnore] public bool TargetJobDescriptionIdSpecified { get; private set; }
    [JsonIgnore] public bool TargetDateSpecified { get; private set; }
    [JsonIgnore] public bool ActiveSpecified { get; private set; }
}

public sealed record CareerGoalResponse(
    Guid Id,
    string TargetRole,
    string Seniority,
    string? Industry,
    string? TargetCompany,
    Guid? TargetJobDescriptionId,
    DateOnly? TargetDate,
    bool Active,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
