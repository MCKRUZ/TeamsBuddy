import { Component } from '@angular/core';
import { Panel } from 'primeng/panel';
import { MeetingControlsComponent } from '../meeting-controls/meeting-controls.component';
import { TranscriptPanelComponent } from '../transcript-panel/transcript-panel.component';
import { QuestionPanelComponent } from '../question-panel/question-panel.component';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [
    Panel,
    MeetingControlsComponent,
    TranscriptPanelComponent,
    QuestionPanelComponent
  ],
  templateUrl: './dashboard.component.html'
})
export class DashboardComponent {}
