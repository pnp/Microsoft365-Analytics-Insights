// Mirrors Web/Models/SystemStatusApiModel.cs (returned by api/SystemStatus).

export interface NamedCount {
  name: string;
  count: number;
}

export interface SystemStatus {
  buildLabel: string | null;
  hasValidConfig: boolean;
  /** Record counts for the main / interesting tables (home page overview). */
  dataCounts: NamedCount[];
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
