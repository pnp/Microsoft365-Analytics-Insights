// Mirrors Web/Models/SystemStatusApiModel.cs (returned by api/SystemStatus).

export interface SystemStatus {
  buildLabel: string | null;
  hasValidConfig: boolean;
  hitCount: number;
  activityCount: number;
  teamsCount: number;
  teamsBeingTrackedCount: number;
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
  configJson: string | null;
}
