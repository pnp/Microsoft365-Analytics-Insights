# Copilot Instructions

## Git workflow
- Never commit. Never push. Make file changes only.
- Wait for the user to explicitly say "commit", "commit and push", or similar before running any `git commit` / `git push`. "Commit and push" given for one change does not extend to subsequent changes — ask again each time.
- This applies to all branches, including `dev`, and to the sibling wiki repo at `V:\Repos\Microsoft365-Analytics-Insights.wiki`.

## Project Guidelines
- User prefers to keep the existing InsertBatch row-by-row implementation rather than replacing it with SqlBulkCopy.

## NuGet Package Management
- When NuGet packages are added or updated, always update binding redirects in both App.Template.config and App.config for all affected projects (including test projects). App.config is generated dynamically from App.Template.config at build time, so App.Template.config is the source of truth.
- When updating Azure SDK packages in this solution, be aware that CloudInstallEngine targets .NET Standard 2.0 while App.ControlPanel.Engine and test projects target .NET Framework 4.8. Upgrading packages in CloudInstallEngine can cause CS1705 assembly version mismatch errors in consuming .NET Framework projects that need matching direct PackageReference additions and binding redirect updates.

## Azure Cache for Redis
- For AAD/Entra ID token-based authentication with Azure Cache for Redis (classic), ensure the following:
  - Set `redisConfiguration.aad-enabled` to "true" via ARM API version 2023-05-01-preview or later.
  - Create a Redis Access Policy Assignment (e.g., "Data Owner") for the service principal.
- Note that the "Redis Cache Contributor" RBAC role is control-plane only and does NOT grant data-plane access.
- For Redis RBAC fallback in this codebase, use `ClientSecretCredential` with the runtime account from config (tenantId, clientId, clientSecret) — NOT `DefaultAzureCredential` or managed identity.
- The Azure.ResourceManager.Redis SDK version 1.1.0 uses an API version too old to support `aad-enabled` configuration. To set `aad-enabled` on Redis, use the ARM REST API directly with api-version 2023-08-01 or later instead of the SDK.