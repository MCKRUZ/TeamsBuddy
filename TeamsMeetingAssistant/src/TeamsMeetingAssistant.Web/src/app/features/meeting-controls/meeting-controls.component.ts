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

  meetingId = signal('');
  loading = signal(false);
  errorMessage = signal('');

  get statusSeverity(): 'success' | 'warn' | 'danger' | 'info' {
    const s = this.state.session()?.status;
    if (s === 'Active') return 'success';
    if (s === 'Pending') return 'warn';
    if (s === 'Error') return 'danger';
    return 'info';
  }

  async start(): Promise<void> {
    const id = this.meetingId().trim();
    if (!id) {
      this.errorMessage.set('Please enter a Meeting ID.');
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
      this.errorMessage.set('Failed to start monitoring. Check the Meeting ID.');
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
      this.loading.set(false);
    }
  }
}
