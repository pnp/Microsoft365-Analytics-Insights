// Mirrors Web/Models/InstallLogModels.cs (returned by api/InstallLog).

export interface InstallLogEntry {
  id: number;
  dateApplied: string;
  installedByUser: string | null;
  messages: string | null;
  configJson: string | null;
  /** True for the most recently applied entry (the current configuration). */
  isCurrent: boolean;
}
