// Mirrors Web/Models/SystemStatusApiModel.cs (returned by api/SystemStatus).

export interface NamedCount {
  /** Stable identifier for the figure (e.g. "auditEvents"); drives the icon, independent of the label. */
  key: string;
  name: string;
  count: number;
  /** One short line saying where the number comes from. */
  hint: string | null;
}

/**
 * The part of api/SystemStatus every signed-in reader gets - the Insights overview's figures. Mirrors
 * SystemStatusInsightsModel. A reader without the Administration permission receives only these.
 */
export interface SystemStatusInsights {
  buildLabel: string | null;
  /** Record counts, limited to the figures whose import is switched on for this deployment. */
  dataCounts: NamedCount[];
  /** Friendly names of the imports switched on for this deployment. */
  enabledImports: string[];
  /** False when the import settings couldn't be read, in which case every figure is shown. */
  importSettingsKnown: boolean;
}

/**
 * The whole reply, for a reader holding the Administration permission. Everything beyond
 * `SystemStatusInsights` is absent - not false or null - for anyone else, so only the Administration
 * area may read it.
 */
export interface SystemStatus extends SystemStatusInsights {
  hasValidConfig: boolean;
  webhookEndpointUrl: string | null;
  callsImportEnabled: boolean;
  /** Disabled | Active | Missing | Error */
  callWebhookState: string;
  callWebhookExpiry: string | null;
  callWebhookStatusDetail: string | null;
  webAppConfigSQL: string | null;
  /** The storage account holding the runtime state table (account name only); null when not configured. */
  webAppConfigStorage: string | null;
  webAppConfigCognitive: string | null;
  cognitiveServiceEnabled: boolean;
  webAppConfigServiceBus: string | null;
}
