import { Injectable, inject, OnDestroy } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { TranscriptSegment } from '../models/transcript-segment.model';
import { QuestionSuggestion } from '../models/question-suggestion.model';
import { MeetingStateService } from './meeting-state.service';
import { environment } from '../../../environments/environment';

@Injectable({ providedIn: 'root' })
export class SignalRService implements OnDestroy {
  private readonly state = inject(MeetingStateService);
  private connection: signalR.HubConnection | null = null;

  readonly transcriptUpdate$ = new Subject<TranscriptSegment[]>();
  readonly questionSuggestions$ = new Subject<QuestionSuggestion[]>();

  async connect(meetingId: string): Promise<void> {
    if (this.connection) {
      await this.disconnect();
    }

    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(environment.hubUrl)
      .withAutomaticReconnect()
      .build();

    this.connection.on('ReceiveTranscriptUpdate', (segments: TranscriptSegment[]) => {
      this.state.appendTranscriptSegments(segments);
      this.transcriptUpdate$.next(segments);
    });

    this.connection.on('ReceiveQuestionSuggestions', (questions: QuestionSuggestion[]) => {
      this.state.setQuestions(questions);
      this.questionSuggestions$.next(questions);
    });

    this.connection.onreconnecting(() => {
      this.state.setConnectionStatus('reconnecting');
    });

    this.connection.onreconnected(() => {
      this.state.setConnectionStatus('connected');
      this.joinMeeting(meetingId);
    });

    this.connection.onclose(() => {
      this.state.setConnectionStatus('disconnected');
    });

    await this.connection.start();
    this.state.setConnectionStatus('connected');
    await this.joinMeeting(meetingId);
  }

  async disconnect(): Promise<void> {
    if (this.connection) {
      await this.connection.stop();
      this.connection = null;
    }
    this.state.setConnectionStatus('disconnected');
  }

  private async joinMeeting(meetingId: string): Promise<void> {
    if (this.connection?.state === signalR.HubConnectionState.Connected) {
      await this.connection.invoke('JoinMeeting', meetingId);
    }
  }

  ngOnDestroy(): void {
    this.disconnect();
  }
}
