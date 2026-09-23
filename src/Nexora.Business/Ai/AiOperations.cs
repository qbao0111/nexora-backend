namespace Nexora.Business.Ai;

public static class AiOperations
{
    public const string ScoreScale = "0-100";

    public static readonly ResumeProfileOperation ResumeProfile = new();
    public static readonly ResumeAnalysisOperation ResumeAnalysis = new();
    public static readonly FieldBenchmarkResumeAnalysisOperation ResumeAnalysisFieldBenchmark = new();
    public static readonly InterviewFirstQuestionOperation InterviewFirstQuestion = new();
    public static readonly InterviewEvaluateOperation InterviewEvaluate = new();
    public static readonly InterviewFollowupOperation InterviewFollowup = new();
    public static readonly InterviewReportOperation InterviewReport = new();
    public static readonly ScenarioEvaluateOperation ScenarioEvaluate = new();
    public static readonly StarEvaluateOperation StarEvaluate = new();
}
