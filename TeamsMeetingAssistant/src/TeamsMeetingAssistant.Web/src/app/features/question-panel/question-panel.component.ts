import { Component, inject } from '@angular/core';
import { PercentPipe } from '@angular/common';
import { Card } from 'primeng/card';
import { Tag } from 'primeng/tag';
import { ProgressBar } from 'primeng/progressbar';
import { MeetingStateService } from '../../core/services/meeting-state.service';
import { QuestionCategory } from '../../core/models/question-suggestion.model';

@Component({
  selector: 'app-question-panel',
  standalone: true,
  imports: [PercentPipe, Card, Tag, ProgressBar],
  templateUrl: './question-panel.component.html'
})
export class QuestionPanelComponent {
  readonly state = inject(MeetingStateService);

  categorySeverity(category: QuestionCategory): 'success' | 'info' | 'warn' | 'danger' {
    const map: Record<QuestionCategory, 'success' | 'info' | 'warn' | 'danger'> = {
      'Deep-dive': 'info',
      'Follow-up': 'success',
      'Clarification': 'warn',
      'Summary': 'danger'
    };
    return map[category] ?? 'info';
  }

  confidencePct(score: number): number {
    return Math.round(score * 100);
  }
}
