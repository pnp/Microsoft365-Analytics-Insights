export type CopilotAuditBackfillState = 'queued' | 'running' | 'completed' | 'completedWithGaps' | 'failed' | 'cancelled';
export type CopilotAuditBackfillPermissionStatus = 'unknown' | 'granted' | 'missing' | 'noIdentity';

export interface CopilotAuditBackfillJob {
  id: number;
  state: CopilotAuditBackfillState;
  requestedBy: string | null;
  createdUtc: string;
  updatedUtc: string;
  startedUtc: string | null;
  completedUtc: string | null;
  startUtc: string;
  endUtc: string;
  pendingSlices: number;
  inFlightSlices: number;
  slicesSubmitted: number;
  slicesCompleted: number;
  slicesSplit: number;
  completedDays: string[];
  failedDays: string[];
  incompleteDays: string[];
  recordsSeen: number;
  recordsImported: number;
  recordsAlreadyPresent: number;
  permissionStatus: CopilotAuditBackfillPermissionStatus;
  copilotImportEnabled: boolean;
  lastErrorCode: string | null;
  cancelRequested: boolean;
  currentSliceStartUtc: string | null;
  currentSliceEndUtc: string | null;
}

export interface CopilotAuditBackfillStatus {
  stateDurable: boolean;
  copilotImportEnabled: boolean;
  latestJob: CopilotAuditBackfillJob | null;
}
