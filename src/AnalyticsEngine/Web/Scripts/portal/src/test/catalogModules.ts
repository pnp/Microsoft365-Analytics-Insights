import type { Catalog } from '../i18n/translate';
import type { Language } from '../i18n';
import { EN_MODULES } from '../i18n';

import esApp from '../i18n/catalog/es/app';
import esCommon from '../i18n/catalog/es/common';
import esCharts from '../i18n/catalog/es/charts';
import esOverview from '../i18n/catalog/es/overview';
import esReports from '../i18n/catalog/es/reports';
import esCopilot from '../i18n/catalog/es/copilotAdoption';
import esCopilotCowork from '../i18n/catalog/es/copilotAdoptionCowork';
import esCopilotAgents from '../i18n/catalog/es/copilotAdoptionAgents';
import esCopilotUsers from '../i18n/catalog/es/copilotAdoptionUsers';
import esTeamsExplorer from '../i18n/catalog/es/teamsExplorer';
import esWebActivity from '../i18n/catalog/es/webActivity';
import esLicenceActivity from '../i18n/catalog/es/licenceActivity';
import esAgentCosts from '../i18n/catalog/es/agentCosts';
import esDlp from '../i18n/catalog/es/dlp';
import esHealth from '../i18n/catalog/es/health';
import esAdmin from '../i18n/catalog/es/admin';

/**
 * The catalog with its module boundaries intact, for the tests that are about those boundaries -
 * "is every key namespaced to its own area?", "does any key appear in two modules?".
 *
 * Kept out of `src/i18n/catalog/index.ts` because that file is in the production bundle, and this
 * imports every Spanish module statically - the exact thing the lazy loading there exists to
 * avoid. A test helper is never reachable from `main.tsx`, so it costs a reader nothing.
 *
 * Adding a catalog module means adding it here too. `catalog.test.ts` fails if the two lists
 * disagree, so it cannot be forgotten silently.
 */
const ES_MODULES = {
  app: esApp,
  common: esCommon,
  charts: esCharts,
  overview: esOverview,
  reports: esReports,
  copilotAdoption: esCopilot,
  copilotAdoptionCowork: esCopilotCowork,
  copilotAdoptionAgents: esCopilotAgents,
  copilotAdoptionUsers: esCopilotUsers,
  teamsExplorer: esTeamsExplorer,
  webActivity: esWebActivity,
  licenceActivity: esLicenceActivity,
  agentCosts: esAgentCosts,
  dlp: esDlp,
  health: esHealth,
  admin: esAdmin,
} as const;

export function catalogModulesFor(language: Language): Record<string, Catalog> {
  const modules = language === 'en' ? EN_MODULES : ES_MODULES;
  return modules as unknown as Record<string, Catalog>;
}
