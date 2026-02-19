import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { Toolbar } from 'primeng/toolbar';
import { MeetingStateService } from './core/services/meeting-state.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, Toolbar],
  template: `
    <p-toolbar styleClass="mb-0">
      <ng-template pTemplate="start">
        <span class="font-bold text-lg">🎙 TeamsBuddy</span>
      </ng-template>
      <ng-template pTemplate="end">
        <span>
          <span class="connection-dot" [class]="state.connectionStatus()"></span>
          {{ connectionLabel() }}
        </span>
      </ng-template>
    </p-toolbar>
    <router-outlet />
  `
})
export class AppComponent {
  constructor(public state: MeetingStateService) {}

  connectionLabel(): string {
    const status = this.state.connectionStatus();
    return status === 'connected' ? 'Connected'
      : status === 'reconnecting' ? 'Reconnecting...'
      : 'Disconnected';
  }
}
