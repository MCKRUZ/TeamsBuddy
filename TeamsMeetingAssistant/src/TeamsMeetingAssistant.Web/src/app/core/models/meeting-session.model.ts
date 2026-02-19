export type MeetingStatus = 'Pending' | 'Active' | 'Completed' | 'Error';

export interface MeetingSession {
  meetingId: string;
  organizerEmail: string;
  startTime: string;
  endTime: string | null;
  isTranscriptionEnabled: boolean;
  activeTranscriptId: string | null;
  lastProcessedTime: string;
  status: MeetingStatus;
}
