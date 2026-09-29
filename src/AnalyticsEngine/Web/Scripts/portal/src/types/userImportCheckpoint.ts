// Mirrors Web/Models/UserImport/UserImportCheckpointModels.cs (returned by api/UserImportCheckpoint).

/**
 * The facts behind the Administration > User import page. Facts only: the page writes every sentence, so it
 * reads in the reader's language. The checkpoint's value never leaves the server - only whether one is stored.
 */
export interface UserImportCheckpointStatus {
  /** Without Redis the checkpoint is never saved, so every user import reads every user. */
  redisConfigured: boolean;
  /** The `GraphUsersMetadata` import switch; null when the import settings couldn't be read. */
  userImportEnabled: boolean | null;
  /** Whether a `/users/delta` token is stored. */
  checkpointStored: boolean;
  /** The Redis key that holds the checkpoint. Data, so it is shown verbatim, never translated. */
  checkpointKey: string;
  /** ISO 8601 UTC time the user import last completed, or null when none is recorded. */
  lastCompletedUtc: string | null;
  /** Minimum hours between user imports; 0 means it runs on every import cycle. */
  intervalHours: number;
}

/** What a clear actually removed, so the page can say so rather than assume. */
export interface UserImportCheckpointClearResult {
  /** A stored checkpoint existed and was deleted. */
  checkpointCleared: boolean;
  /** The last-completed stamp existed and was deleted, so the import runs on the next cycle. */
  lastCompletedCleared: boolean;
}
