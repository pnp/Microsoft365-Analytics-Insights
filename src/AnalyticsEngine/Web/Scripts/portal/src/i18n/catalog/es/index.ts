import type { Catalog } from '../../translate';

import app from './app';
import common from './common';
import charts from './charts';
import overview from './overview';
import reports from './reports';
import copilotAdoption from './copilotAdoption';
import copilotAdoptionCowork from './copilotAdoptionCowork';
import copilotAdoptionAgents from './copilotAdoptionAgents';
import copilotAdoptionUsers from './copilotAdoptionUsers';
import teamsExplorer from './teamsExplorer';
import webActivity from './webActivity';
import licenceActivity from './licenceActivity';
import agentCosts from './agentCosts';
import dlp from './dlp';
import errors from './errors';
import health from './health';
import admin from './admin';
import userOrgs from './userOrgs';

/**
 * Every Spanish module, as one object - and, because this is the only file `loadCatalog`
 * dynamically imports for Spanish, as **one chunk**.
 *
 * That last part is the point. Importing the seventeen modules individually from `loadCatalog`
 * made Vite emit seventeen chunks and the browser make seventeen requests, all gathered with
 * `Promise.all`. `Promise.all` rejects on the first failure, so a single dropped request - one
 * proxy hiccup, one stale filename after a redeploy - took the entire Spanish catalog down and
 * fell the reader back to English. Seventeen chances to fail instead of one, for no benefit: a
 * language is all-or-nothing anyway, since a page mixing two of them is the defect this whole
 * feature exists to prevent.
 *
 * Adding a module means adding it here as well as to `EN_MODULES`. `catalog.test.ts` compares the
 * two module lists, so forgetting is a test failure rather than a language that is quietly missing
 * an area.
 */
export const ES_MODULES = {
  app,
  common,
  charts,
  overview,
  reports,
  copilotAdoption,
  copilotAdoptionCowork,
  copilotAdoptionAgents,
  copilotAdoptionUsers,
  teamsExplorer,
  webActivity,
  licenceActivity,
  agentCosts,
  dlp,
  errors,
  health,
  admin,
  userOrgs,
} as const;

export default ES_MODULES as unknown as Record<string, Catalog>;
