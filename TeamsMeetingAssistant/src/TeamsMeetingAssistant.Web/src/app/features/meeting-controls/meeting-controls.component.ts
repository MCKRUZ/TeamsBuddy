import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { InputText } from 'primeng/inputtext';
import { Button } from 'primeng/button';
import { Tag } from 'primeng/tag';
import { MeetingService } from '../../core/services/meeting.service';
import { SignalRService } from '../../core/services/signalr.service';
import { MeetingStateService } from '../../core/services/meeting-state.service';

@Component({
  selector: 'app-meeting-controls',
  standalone: true,
  imports: [FormsModule, InputText, Button, Tag],
  templateUrl: './meeting-controls.component.html'
})
export class MeetingControlsComponent {
  private readonly meetingService = inject(MeetingService);
  private readonly signalRService = inject(SignalRService);
  readonly state = inject(MeetingStateService);

  joinUrl = signal('');
  userEmail = signal('');
  resolvedMeetingId = signal('');
  loading = signal(false);
  lookingUp = signal(false);
  errorMessage = signal('');

  get statusSeverity(): 'success' | 'warn' | 'danger' | 'info' {
    const s = this.state.session()?.status;
    if (s === 'Active') return 'success';
    if (s === 'Pending') return 'warn';
    if (s === 'Error') return 'danger';
    return 'info';
  }

  async lookup(): Promise<void> {
    const url = this.joinUrl().trim();
    const email = this.userEmail().trim();
    if (!url || !email) {
      this.errorMessage.set('Please enter both a Teams join URL and organizer email.');
      return;
    }
    this.errorMessage.set('');
    this.lookingUp.set(true);
    try {
      const result = await this.meetingService.lookupMeeting(email, url).toPromise();
      if (result?.meetingId) {
        this.resolvedMeetingId.set(result.meetingId);
      } else {
        this.errorMessage.set('No meeting found for this URL.');
      }
    } catch {
      this.errorMessage.set('Lookup failed. Check the join URL and email.');
    } finally {
      this.lookingUp.set(false);
    }
  }

  async start(): Promise<void> {
    const id = this.resolvedMeetingId().trim();
    if (!id) {
      this.errorMessage.set('Please look up a meeting first.');
      return;
    }
    this.errorMessage.set('');
    this.loading.set(true);
    try {
      const session = await this.meetingService.startMonitoring(id).toPromise();
      if (session) {
        this.state.setSession(session);
        await this.signalRService.connect(id);
      }
    } catch {
      this.errorMessage.set('Failed to start monitoring.');
    } finally {
      this.loading.set(false);
    }
  }

  async stop(): Promise<void> {
    const session = this.state.session();
    if (!session) return;
    this.loading.set(true);
    try {
      await this.meetingService.stopMonitoring(session.meetingId).toPromise();
    } finally {
      await this.signalRService.disconnect();
      this.state.setSession(null);
      this.resolvedMeetingId.set('');
      this.loading.set(false);
    }
  }
}
