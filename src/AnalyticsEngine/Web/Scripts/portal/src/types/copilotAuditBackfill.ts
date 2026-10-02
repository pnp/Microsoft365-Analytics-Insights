export type CopilotAuditBackfillState = 'queued' | 'running' | 'completed' | 'failed' | 'cancelled';
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
  slicesSubmitted: number;
  slicesCompleted: number;
  slicesSplit: number;
  completedDays: string[];
  recordsSeen: number;
  recordsImported: number;
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
