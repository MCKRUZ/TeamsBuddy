import { Injectable, signal, computed } from '@angular/core';
import { MeetingSession } from '../models/meeting-session.model';
import { TranscriptSegment } from '../models/transcript-segment.model';
import { QuestionSuggestion } from '../models/question-suggestion.model';

const MAX_TRANSCRIPT_ENTRIES = 200;

@Injectable({ providedIn: 'root' })
export class MeetingStateService {
  readonly session = signal<MeetingSession | null>(null);
  readonly transcript = signal<TranscriptSegment[]>([]);
  readonly questions = signal<QuestionSuggestion[]>([]);
  readonly connectionStatus = signal<'connected' | 'disconnected' | 'reconnecting'>('disconnected');

  readonly isMonitoring = computed(() => this.session()?.status === 'Active');

  setSession(session: MeetingSession | null): void {
    this.session.set(session);
    if (!session) {
      this.transcript.set([]);
      this.questions.set([]);
    }
  }

  appendTranscriptSegments(segments: TranscriptSegment[]): void {
    this.transcript.update(current => {
      const combined = [...current, ...segments];
      // Cap at MAX_TRANSCRIPT_ENTRIES (newest entries kept)
      return combined.length > MAX_TRANSCRIPT_ENTRIES
        ? combined.slice(combined.length - MAX_TRANSCRIPT_ENTRIES)
        : combined;
    });
  }

  setQuestions(questions: QuestionSuggestion[]): void {
    this.questions.set(questions);
  }

  setConnectionStatus(status: 'connected' | 'disconnected' | 'reconnecting'): void {
    this.connectionStatus.set(status);
  }
}
