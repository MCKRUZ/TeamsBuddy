import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { MeetingSession } from '../models/meeting-session.model';
import { environment } from '../../../environments/environment';

@Injectable({ providedIn: 'root' })
export class MeetingService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiUrl}/api/meeting`;

  lookupMeeting(userEmail: string, joinUrl: string): Observable<{ meetingId: string }> {
    return this.http.post<{ meetingId: string }>(`${this.base}/lookup`, { userEmail, joinUrl });
  }

  startMonitoring(meetingId: string): Observable<MeetingSession> {
    const formData = new FormData();
    formData.append('meetingId', meetingId);
    return this.http.post<MeetingSession>(`${this.base}/start`, formData);
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
