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

public sealed class InterviewQuestionGenerator(IResumeContextBuilder resumeContextBuilder, IStructuredAiExecutor structuredAiExecutor)
{
    internal async Task<IReadOnlyCollection<PreparedInterviewQuestion>> GeneratePreparedQuestionsAsync(
        InterviewSession session,
        ResumeProfile? profile,
        int startSequence,
        int endSequence,
        bool hasUsableResumeContext,
        CancellationToken cancellationToken,
        IReadOnlyCollection<(string Topic, string Content)>? existingQuestions = null)
    {
        var prepared = new List<PreparedInterviewQuestion>();
        var previousQuestions = existingQuestions?.ToList() ?? [];
        for (var sequence = startSequence; sequence <= endSequence; sequence++)
        {
            var topic = sequence == 1 && !string.IsNullOrWhiteSpace(session.FocusTopic)
                ? session.FocusTopic!
                : InterviewQuestionValues.TopicForSequence(
                        session.InterviewType,
                        sequence,
                        hasUsableResumeContext,
                        session.JobDescription is not null);
            var context = resumeContextBuilder.BuildInterviewQuestionContext(
                session.Role,
                session.Seniority,
                session.InterviewType,
                session.Difficulty,
                session.JobDescription?.Content,
                profile,
                sequence,
                topic,
                previousQuestions);
            var result = await structuredAiExecutor.ExecuteAsync<GeneratedQuestion>(
                AiOperations.InterviewFirstQuestion,
                context,
                new AiOperationContext(
                    session.Id.ToString("N"),
                    session.UserId,
                    Metadata: new Dictionary<string, string>
                    {
                        ["questionSequence"] = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["questionKind"] = InterviewQuestionValues.Primary,
                        ["questionTopic"] = topic
                    },
                    PreviousQuestions: previousQuestions.Select(item => item.Content).ToArray()),
                cancellationToken);
            var content = result.Value.Content?.Trim() ?? string.Empty;
            if (content.Length == 0) throw InvalidAiOutput();
            prepared.Add(new PreparedInterviewQuestion(
                sequence,
                topic,
                content[..Math.Min(content.Length, 2_000)],
                result.PromptVersion,
                result.ModelVersion));
            previousQuestions.Add((topic, content));
        }

        return prepared;
    }


}
internal sealed record PreparedInterviewQuestion(int Sequence, string Topic, string Content, string PromptVersion, string ModelVersion);
