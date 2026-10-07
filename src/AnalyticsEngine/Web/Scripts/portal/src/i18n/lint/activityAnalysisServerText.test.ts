// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

import { EN_CATALOG } from '../catalog';
import { ACTIVITY_ANALYSIS_ERROR_KEYS } from '../../api/activityAnalysisApi';

/**
 * The Activity analysis API reports facts - metric keys, category keys, unavailable reasons and error
 * codes - and the portal writes the sentences. This keeps the two in step: every key the server can
 * send has its words in the catalog, and every code the portal words is one the server still sends.
 *
 * The server half (`Web/Controllers/ActivityAnalysisAPIController.cs` and
 * `Common/Entities/ActivityAnalysis/`) was built alongside the portal half, so on a branch that has
 * only the portal the checks against it have nothing to read and are skipped; wherever both halves
 * are present they run. The checks themselves are proved on a synthetic source either way.
 */

const WEB = join(process.cwd(), '..', '..');
const CONTROLLER = join(WEB, 'Controllers', 'ActivityAnalysisAPIController.cs');
const ENTITIES = join(WEB, '..', 'Common', 'Entities', 'ActivityAnalysis');
const SERVER_PRESENT = existsSync(CONTROLLER) || existsSync(ENTITIES);

/** The API's category keys (contract section 2), in the order the slicer offers them. */
const CATEGORIES = ['teams', 'outlook', 'onedrive', 'sharepoint', 'copilot', 'vivaEngage'];

/** The reasons availability can give for the page being unavailable. */
const REASONS = ['notInstalled', 'noData'];

function serverSource(): string {
  const files = [
    ...(existsSync(CONTROLLER) ? [CONTROLLER] : []),
    ...(existsSync(ENTITIES)
      ? readdirSync(ENTITIES, { recursive: true })
          .map(String)
          .filter((file) => file.endsWith('.cs'))
          .map((file) => join(ENTITIES, file))
      : []),
  ];
  return files.map((file) => readFileSync(file, 'utf8')).join('\n');
}

/** A quoted metric key: a category, a dot, and one or two camelCase parts (`copilot.app.word`). */
const METRIC_KEY = /"((?:teams|outlook|onedrive|sharepoint|vivaEngage|copilot)\.[a-z][A-Za-z0-9]*(?:\.[a-z][A-Za-z0-9]*)?)"/g;

function metricKeysIn(source: string): string[] {
  return [...new Set([...source.matchAll(METRIC_KEY)].map((m) => m[1]))];
}

function unlabelledMetrics(source: string): string[] {
  return metricKeysIn(source).filter((key) => !(`activityAnalysis.metric.${key}` in EN_CATALOG));
}

/** Codes, categories and reasons the portal words that the source never sends. */
function staleCodes(source: string): string[] {
  return [...ACTIVITY_ANALYSIS_ERROR_KEYS.keys(), ...CATEGORIES, ...REASONS].filter((code) => !source.includes(`"${code}"`));
}

/**
 * Error codes the server sends that the portal has no words for: every constant of
 * `ActivityAnalysisErrorCodes`, and the controller's own catch-all `FailedCode`. Without this, a new
 * refusal would reach a Spanish reader as the generic "the request wasn't valid".
 */
function unwordedCodes(source: string): string[] {
  const block = /class ActivityAnalysisErrorCodes\s*\{([\s\S]*?)\}/.exec(source)?.[1] ?? '';
  const codes = [...block.matchAll(/=\s*"([^"]+)"/g)].map((m) => m[1]);
  const failed = /const string FailedCode\s*=\s*"([^"]+)"/.exec(source)?.[1];
  return [...codes, ...(failed ? [failed] : [])].filter((code) => !ACTIVITY_ANALYSIS_ERROR_KEYS.has(code));
}

describe.skipIf(!SERVER_PRESENT)('Activity analysis keys the server sends', () => {
  const source = SERVER_PRESENT ? serverSource() : '';

  it('finds the server’s metric catalogue', () => {
    expect(metricKeysIn(source).length, 'Metric keys found in the server source').toBeGreaterThanOrEqual(58);
  });

  it('has a label for every metric key in the server catalogue', () => {
    expect(unlabelledMetrics(source), 'Add activityAnalysis.metric.<key> to the en and es catalogs').toEqual([]);
  });

  it('words only codes, categories and reasons the server still sends', () => {
    expect(staleCodes(source)).toEqual([]);
  });

  it('words every error code the server can send', () => {
    expect(source, 'ActivityAnalysisErrorCodes not found').toContain('class ActivityAnalysisErrorCodes');
    expect(unwordedCodes(source), 'Map it in ACTIVITY_ANALYSIS_ERROR_KEYS with en and es text').toEqual([]);
  });
});

describe('Activity analysis key checks', () => {
  it('has a label for every category the API defines', () => {
    for (const category of CATEGORIES) expect(EN_CATALOG).toHaveProperty([`activityAnalysis.category.${category}`]);
  });

  describe('the check itself', () => {
    const sample = `
      public static readonly Metric[] All = {
        new Metric("teams.calls", "Teams Calls", "teams", Unit.Count, core: true),
        new Metric("copilot.app.word", "Copilot App Word", "copilot", Unit.Count, core: false),
        new Metric("teams.holograms", "Teams Holograms", "teams", Unit.Count, core: false),
      };
      // "contoso.sharepoint.com" is not a metric key.
      return Error(400, "invalidPeriod");`;

    it('finds metric keys, including three-part ones, and nothing else', () => {
      expect(metricKeysIn(sample)).toEqual(['teams.calls', 'copilot.app.word', 'teams.holograms']);
    });

    it('reports a metric with no label', () => {
      expect(unlabelledMetrics(sample)).toEqual(['teams.holograms']);
    });

    it('reports a code the portal words but the source no longer sends', () => {
      expect(staleCodes(sample)).toContain('invalidMetric');
      expect(staleCodes(sample)).not.toContain('invalidPeriod');
    });

    it('reports a code the server sends that the portal does not word', () => {
      const codes = `
        public static class ActivityAnalysisErrorCodes
        {
            public const string InvalidPeriod = "invalidPeriod";
            public const string Holograms = "hologramsRefused";
        }
        internal const string FailedCode = "activityAnalysisFailed";`;
      expect(unwordedCodes(codes)).toEqual(['hologramsRefused']);
    });
  });
});
