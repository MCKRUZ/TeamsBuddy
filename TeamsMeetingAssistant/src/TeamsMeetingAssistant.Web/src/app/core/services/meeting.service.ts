import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { MeetingSession } from '../models/meeting-session.model';
import { environment } from '../../../environments/environment';

@Injectable({ providedIn: 'root' })
export class MeetingService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiUrl}/api/meeting`;

  startMonitoring(meetingId: string): Observable<MeetingSession> {
    return this.http.post<MeetingSession>(`${this.base}/start`, { meetingId });
  }

  stopMonitoring(meetingId: string): Observable<void> {
    return this.http.post<void>(`${this.base}/stop`, { meetingId });
  }

  getSessions(): Observable<MeetingSession[]> {
    return this.http.get<MeetingSession[]>(`${this.base}/sessions`);
  }

  getSession(meetingId: string): Observable<MeetingSession> {
    return this.http.get<MeetingSession>(`${this.base}/${meetingId}`);
  }
}
