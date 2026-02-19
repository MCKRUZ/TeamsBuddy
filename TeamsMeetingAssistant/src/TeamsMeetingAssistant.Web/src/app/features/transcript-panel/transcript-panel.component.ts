import { Component, inject, AfterViewChecked, ElementRef, ViewChild } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ScrollPanel } from 'primeng/scrollpanel';
import { MeetingStateService } from '../../core/services/meeting-state.service';

@Component({
  selector: 'app-transcript-panel',
  standalone: true,
  imports: [DatePipe, ScrollPanel],
  templateUrl: './transcript-panel.component.html'
})
export class TranscriptPanelComponent implements AfterViewChecked {
  readonly state = inject(MeetingStateService);

  @ViewChild('scrollEnd') private scrollEnd!: ElementRef;

  private lastCount = 0;

  ngAfterViewChecked(): void {
    const count = this.state.transcript().length;
    if (count !== this.lastCount) {
      this.lastCount = count;
      this.scrollEnd?.nativeElement?.scrollIntoView({ behavior: 'smooth' });
    }
  }
}
