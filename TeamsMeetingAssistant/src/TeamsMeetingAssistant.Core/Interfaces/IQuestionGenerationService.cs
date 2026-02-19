namespace TeamsMeetingAssistant.Core.Interfaces;

public interface IQuestionGenerationService
{
    Task<List<QuestionSuggestion>> GenerateQuestionsAsync(
        List<TranscriptSegment> recentTranscript,
        QuestionGenerationContext context,
        CancellationToken cancellationToken);
}
