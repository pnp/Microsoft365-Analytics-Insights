// Mirrors Web/Models/SystemStatusApiModel.cs (returned by api/SystemStatus).

export interface NamedCount {
  /** Stable identifier for the figure (e.g. "auditEvents"); drives the icon, independent of the label. */
  key: string;
  name: string;
  count: number;
  /** One short line saying where the number comes from. */
  hint: string | null;
}

export interface SystemStatus {
  buildLabel: string | null;
  hasValidConfig: boolean;
  /** Record counts, limited to the figures whose import is switched on for this deployment. */
  dataCounts: NamedCount[];
  /** Friendly names of the imports switched on for this deployment. */
  enabledImports: string[];
  /** False when the import settings couldn't be read, in which case every figure is shown. */
  importSettingsKnown: boolean;
  webhookEndpointUrl: string | null;
  callsImportEnabled: boolean;
  /** Disabled | Active | Missing | Error */
  callWebhookState: string;
  callWebhookExpiry: string | null;
  callWebhookStatusDetail: string | null;
  webAppConfigSQL: string | null;
  webAppConfigRedis: string | null;
  webAppConfigCognitive: string | null;
  cognitiveServiceEnabled: boolean;
  webAppConfigServiceBus: string | null;
}
