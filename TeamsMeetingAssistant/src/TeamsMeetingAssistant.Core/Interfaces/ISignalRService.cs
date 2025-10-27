namespace TeamsMeetingAssistant.Core.Interfaces;

public interface ISignalRService
{
    Task SendTranscriptUpdateAsync(string meetingId, TranscriptSegment segment);
    Task SendQuestionSuggestionsAsync(string meetingId, List<QuestionSuggestion> suggestions);
    Task NotifyMeetingStatusAsync(string meetingId, string status);
}