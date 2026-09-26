// @vitest-environment node
import { beforeAll, describe, it, expect } from 'vitest';
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

import { loadCatalog, translateStatic, type TFunction } from '..';
import { EN_CATALOG } from '../catalog';
import { COPILOT_ADOPTION_WARNING_KEYS, COWORK_TIER_LABEL_KEYS, TENURE_BASIS_LABEL_KEYS } from '../../components/copilotAdoption/serverText';
import {
  BLOB_CHECKPOINT_REASON_KEYS,
  HEALTH_COMPONENT_LABEL_KEYS,
  translateHealthComponentDetailText,
  translateHealthReasonText,
} from '../../components/health/healthShared';
import { SERVER_PLACEHOLDER_KEYS, serverPlaceholderText } from '../../components/shared/serverPlaceholder';
import { TEAMS_MEETING_BUCKET_LABEL_KEYS, TEAMS_SEGMENT_TEXT_KEYS } from '../../components/teamsExplorer/teamsShared';
import { WEB_ACTIVITY_AVAILABILITY_REASON_KEYS } from '../../components/webActivity/AvailabilityBar';
import { USER_DATA_WORKLOADS_BY_FLAG } from '../../components/userlookup/CategoryRow';
import { ACCOUNTABILITY_DIMENSION_TEXT, ACCOUNTABILITY_EMPTY_SEGMENT_KEYS } from '../../pages/CopilotAdoptionPage';
import { ENABLED_IMPORT_LABELS_BY_SETTING_PROPERTY } from '../../pages/InsightsOverviewPage';
import { OFFICE_PLATFORM_LABEL_KEYS } from '../../pages/ReportsPage';
import { WORKLOADS } from '../../types/licenceActivity';

function sortedUnique(values: string[]): string[] {
  return [...new Set(values)].sort((a, b) => a.localeCompare(b));
}

function catalogKeys(prefix: string): string[] {
  return sortedUnique(Object.keys(EN_CATALOG).filter((key) => key.startsWith(prefix)));
}

function catalogValues(keys: string[]): string[] {
  return sortedUnique(keys.map((key) => EN_CATALOG[key]));
}

function functionBody(source: string, name: string): string {
  const start = source.indexOf(`function ${name}`);
  expect(start, `Could not find function ${name}`).toBeGreaterThanOrEqual(0);
  const brace = source.indexOf('{', start);
  expect(brace, `Could not find function body for ${name}`).toBeGreaterThanOrEqual(0);

  let depth = 0;
  for (let i = brace; i < source.length; i++) {
    const char = source[i];
    if (char === '{') depth++;
    if (char === '}') depth--;
    if (depth === 0) return source.slice(brace + 1, i);
  }

  throw new Error(`Could not find end of function ${name}`);
}

function translationKeysIn(source: string, prefix: string): string[] {
  const escapedPrefix = prefix.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return sortedUnique([...source.matchAll(new RegExp(`['"](${escapedPrefix}[^'"]+)['"]`, 'g'))].map((m) => m[1]));
}

function labelKeyMapFromFunction(source: string, functionName: string, prefix: string): Record<string, string> {
  const body = functionBody(source, functionName);
  const map: Record<string, string> = {};
  const entry =
    /(?:^|\n)\s*(?:(\w+)|'([^']+)'|\[\[([^\]]+)\]\.join\(' '\)\])\s*:\s*'([^']+)'/g;

  for (const match of body.matchAll(entry)) {
    const key = match[4];
    if (!key.startsWith(prefix)) continue;

    const label = match[1]
      ?? match[2]
      ?? [...match[3].matchAll(/'([^']+)'/g)].map((word) => word[1]).join(' ');
    map[label] = key;
  }

  expect(Object.keys(map).length, `Could not extract ${functionName}'s ${prefix} map`).toBeGreaterThan(0);
  return map;
}

function expectServerLabelsCoveredBySpaMap(serverLabels: string[], spaMap: Record<string, string>, context: string): void {
  const uniqueServerLabels = sortedUnique(serverLabels);
  expect(uniqueServerLabels.length, `${context}: server extraction matched no labels`).toBeGreaterThan(0);

  const missing = uniqueServerLabels.filter((label) => !(label in spaMap));
  const orphans = sortedUnique(Object.keys(spaMap).filter((label) => !uniqueServerLabels.includes(label)));
  const wrongCatalogValue = uniqueServerLabels
    .filter((label) => label in spaMap)
    .filter((label) => EN_CATALOG[spaMap[label]] !== label)
    .map((label) => `${label} -> ${spaMap[label]} -> ${EN_CATALOG[spaMap[label]]}`);

  expect(
    { missing, orphans, wrongCatalogValue },
    `${context}: the C# labels and SPA translation map must be an exact two-way match.`,
  ).toEqual({ missing: [], orphans: [], wrongCatalogValue: [] });
}

/**
 * The overview tiles are named by the server, so their translations are checked against the server.
 *
 * `api/SystemStatus` authors its own English `name` and `hint` for each figure
 * (`SystemStatusAPIController.DataCountDefinition`). The SPA cannot translate a string it did not
 * write, so it maps the model's stable `key` to a catalog entry instead - which means the catalog
 * and that C# list have to agree, and nothing else makes them.
 *
 * The failure this prevents is quiet and was live until someone looked at a Spanish home page: add
 * a figure to the controller and the tile appears in English for every language, with `tsc`, the
 * untranslated-text check and the catalog checks all green, because from the SPA's point of view
 * there is no string and no missing key - only a `key` it happens not to recognise.
 *
 * So this reads the controller. Crossing the language boundary in a test is unusual, and worth it
 * here: it is the only place the two halves of this contract meet.
 */

/** From `portal` (vitest's cwd) up to the `Web` project, which owns the API controllers. */
const CONTROLLER = join(process.cwd(), '..', '..', 'Controllers', 'SystemStatusAPIController.cs');

/** `new DataCountDefinition("users", "Users", "People discovered by any import",` */
const DEFINITION = /new\s+DataCountDefinition\(\s*"([^"]+)"/g;

function serverKeys(): string[] {
  const source = readFileSync(CONTROLLER, 'utf8');
  return [...source.matchAll(DEFINITION)].map((m) => m[1]);
}

describe('Overview data-count tiles', () => {
  it('finds the controller that defines them', () => {
    // A moved or renamed controller would otherwise make every assertion below vacuously pass.
    expect(() => readFileSync(CONTROLLER, 'utf8')).not.toThrow();
    expect(serverKeys().length).toBeGreaterThanOrEqual(10);
  });

  it('translates every figure the server can send', () => {
    const missing = serverKeys().flatMap((key) =>
      (['name', 'hint'] as const)
        .map((field) => `overview.dataCount.${key}.${field}`)
        .filter((catalogKey) => !(catalogKey in EN_CATALOG)),
    );

    expect(
      missing,
      'These figures are defined in SystemStatusAPIController but have no catalog entry, so the\n' +
        "tile falls back to the server's English in every language. Add them to\n" +
        'src/i18n/catalog/{en,es}/overview.ts:\n  ' +
        missing.join('\n  '),
    ).toEqual([]);
  });

  it('has no catalog entry for a figure the server no longer sends', () => {
    const known = new Set(serverKeys());
    const orphans = Object.keys(EN_CATALOG)
      .filter((key) => key.startsWith('overview.dataCount.'))
      .map((key) => key.slice('overview.dataCount.'.length).replace(/\.(?:name|hint)$/, ''))
      .filter((key) => !known.has(key));

    expect(
      [...new Set(orphans)],
      'These have catalog entries but are not defined in SystemStatusAPIController - either the\n' +
        'figure was removed, or its key was renamed and the tile is now falling back to English.',
    ).toEqual([]);
  });
});

/**
 * The Reports API writes chart headings, descriptions, value-axis labels and warnings in C#.
 * The SPA translates them by the chart's stable `key`, so the catalog and the controller must stay
 * in lock-step just like the overview tiles above.
 */

/** From `portal` (vitest's cwd) up to the `Web` project, which owns the API controllers. */
const REPORTS_CONTROLLERS = [
  join(process.cwd(), '..', '..', 'Controllers', 'ReportsAPIController.cs'),
  join(process.cwd(), '..', '..', 'Controllers', 'ReportsAPIController.OfficeApps.cs'),
];

/** `RunTimeSeriesAsync("copilot-interactions", "Copilot interactions per week", ...` */
const CHART_CALL = /Run(?:TimeSeries|MultiTimeSeries|GroupedTimeSeries|GroupedCategory|Category|Matrix)Async\(\s*"([^"]+)"/g;
/** Object-initialiser charts which do not flow through a Run* helper. */
const CHART_KEY_PROPERTY = /(?:^|[{\s,])Key\s*=\s*"([^"]+)"/g;
/** `CopilotAttachChart` uses one local constant for several branches. */
const CHART_KEY_CONST = /const\s+string\s+key\s*=\s*"([^"]+)"/g;

const EXTRA_REPORT_CHART_FIELDS: Record<string, string[]> = {
  'copilot-key-phrases': ['warning'],
  'copilot-languages': ['warning'],
  'usage-active-users': ['warning'],
  'office-apps-platform-matrix': ['rowLabel', 'columnLabel'],
  'office-apps-by-department': ['rowLabel', 'columnLabel'],
  'office-apps-department-adoption': ['description.groupFiltered', 'warning'],
  'office-apps-by-domain': ['rowLabel', 'columnLabel'],
  'office-apps-copilot-attach': ['warning', 'warning.confirmFailed', 'warning.noReportRows'],
};

function reportControllerSource(): string {
  return REPORTS_CONTROLLERS.map((path) => readFileSync(path, 'utf8')).join('\n');
}

function reportChartKeys(): string[] {
  const source = reportControllerSource();
  return [
    ...source.matchAll(CHART_CALL),
    ...source.matchAll(CHART_KEY_PROPERTY),
    ...source.matchAll(CHART_KEY_CONST),
  ].map((m) => m[1]);
}

function officePlatformLabels(): string[] {
  const source = readFileSync(join(process.cwd(), '..', '..', 'Controllers', 'ReportsAPIController.OfficeApps.cs'), 'utf8');
  const block = source.match(/OfficePlatformCatalogue\s*=\s*\{([\s\S]*?)\};/)?.[1] ?? '';
  return sortedUnique([...block.matchAll(/new\s+KeyValuePair<string,\s*string>\("([^"]+)"/g)].map((m) => m[1]));
}

function expectedReportCatalogKeys(): string[] {
  return [...new Set(reportChartKeys())].flatMap((key) => [
    `reports.chart.${key}.title`,
    `reports.chart.${key}.description`,
    `reports.chart.${key}.valueLabel`,
    ...(EXTRA_REPORT_CHART_FIELDS[key] ?? []).map((field) => `reports.chart.${key}.${field}`),
  ]);
}

describe('Reports chart metadata', () => {
  it('finds the controllers that define the charts', () => {
    // A moved or renamed controller would otherwise make every assertion below vacuously pass.
    for (const controller of REPORTS_CONTROLLERS) {
      expect(() => readFileSync(controller, 'utf8')).not.toThrow();
    }
    expect(new Set(reportChartKeys()).size).toBeGreaterThanOrEqual(25);
  });

  it('translates every chart metadata field the server can send', () => {
    const missing = expectedReportCatalogKeys().filter((catalogKey) => !(catalogKey in EN_CATALOG));

    expect(
      missing,
      'These report chart metadata fields are defined in ReportsAPIController but have no catalog\n' +
        "entry, so the chart falls back to the server's English in every language. Add them to\n" +
        'src/i18n/catalog/{en,es}/reports.ts:\n  ' +
        missing.join('\n  '),
    ).toEqual([]);
  });

  it('has no catalog entry for a chart the server no longer sends', () => {
    const expected = new Set(expectedReportCatalogKeys());
    const orphans = Object.keys(EN_CATALOG)
      .filter((key) => key.startsWith('reports.chart.'))
      .filter((key) => !['reports.chart.sqlTitle', 'reports.chart.loadError', 'reports.chart.noData'].includes(key))
      .filter((key) => !key.startsWith('reports.chart.error.'))
      .filter((key) => !key.startsWith('reports.chart.warning.series.'))
      .filter((key) => !expected.has(key));

    expect(
      orphans,
      'These report chart catalog entries are not defined in ReportsAPIController - either the\n' +
        'chart was removed, or its key/field was renamed and the page is now falling back to English.',
    ).toEqual([]);
  });
});

describe('Reports Office platform labels', () => {
  it('keeps the translated platform-category map aligned with the server catalogue', () => {
    const serverLabels = officePlatformLabels();
    expect(serverLabels).toEqual(['Mac', 'Mobile', 'Web', 'Windows']);

    const translatableProductCategories = ['Mobile', 'Web'];
    expect(Object.keys(OFFICE_PLATFORM_LABEL_KEYS).sort()).toEqual(translatableProductCategories);
    expect(translatableProductCategories.every((label) => serverLabels.includes(label))).toBe(true);
    expect(Object.values(OFFICE_PLATFORM_LABEL_KEYS).filter((key) => !(key in EN_CATALOG))).toEqual([]);
  });
});

/**
 * Teams Explorer overview judgements are also authored server-side. The SPA translates them by the
 * stable judgement key, then falls back to the server's English for a key this build does not know.
 */
const TEAMS_SCORING = join(
  process.cwd(),
  '..',
  '..',
  '..',
  'Common',
  'Entities',
  'TeamsExplorer',
  'TeamsExplorerScoring.cs',
);

const TEAMS_JUDGEMENT = /new\s+TeamsJudgement\(\s*"([^"]+)"/g;

function teamsJudgementKeys(): string[] {
  const source = readFileSync(TEAMS_SCORING, 'utf8');
  return [...new Set([...source.matchAll(TEAMS_JUDGEMENT)].map((m) => m[1]))];
}

describe('Teams Explorer server-authored judgements', () => {
  it('finds the scoring file that defines them', () => {
    expect(() => readFileSync(TEAMS_SCORING, 'utf8')).not.toThrow();
    expect(teamsJudgementKeys().length).toBeGreaterThanOrEqual(7);
  });

  it('translates every judgement the server can send', () => {
    const missing = teamsJudgementKeys().flatMap((key) => {
      const headline = `teamsExplorer.judgement.${key}.headline`;
      const detailPrefix = `teamsExplorer.judgement.${key}.detail`;
      return [
        ...(headline in EN_CATALOG ? [] : [headline]),
        ...(Object.keys(EN_CATALOG).some((catalogKey) => catalogKey === detailPrefix || catalogKey.startsWith(`${detailPrefix}.`))
          ? []
          : [detailPrefix]),
      ];
    });

    expect(
      missing,
      'These Teams judgements are defined in TeamsExplorerScoring but have no catalog entry, so\n' +
        "the card falls back to the server's English in every language. Add them to\n" +
        'src/i18n/catalog/{en,es}/teamsExplorer.ts:\n  ' +
        missing.join('\n  '),
    ).toEqual([]);
  });

  it('has no catalog entry for a judgement the server no longer sends', () => {
    const known = new Set(teamsJudgementKeys());
    const orphans = Object.keys(EN_CATALOG)
      .filter((key) => key.startsWith('teamsExplorer.judgement.'))
      .filter((key) => !key.includes('.band.'))
      .map((key) => key.slice('teamsExplorer.judgement.'.length).replace(/\.(?:headline|detail(?:\.[^.]+)?)$/, ''))
      .filter((key) => !known.has(key));

    expect(
      [...new Set(orphans)],
      'These have catalog entries but are not defined in TeamsExplorerScoring - either the\n' +
        'judgement was removed, or its key was renamed and the card is now falling back to English.',
    ).toEqual([]);
  });
});

/**
 * Profiling freshness range labels come from ProfilingStatusAPIController with a stable key beside
 * the display label.
 */
const PROFILING_CONTROLLER = join(process.cwd(), '..', '..', 'Controllers', 'ProfilingStatusAPIController.cs');
const PROFILING_SOURCE = /new\s+DateRangeStatSource\(\s*"([^"]+)"/g;
const PROFILING_RANGE = /(?:GetRangeAsync|RunRangeAsync)\(\s*"([^"]+)"/g;

function profilingRangeKeys(): string[] {
  const source = readFileSync(PROFILING_CONTROLLER, 'utf8');
  return [
    ...new Set([
      ...[...source.matchAll(PROFILING_SOURCE)].map((m) => m[1]),
      ...[...source.matchAll(PROFILING_RANGE)].map((m) => m[1]),
    ]),
  ];
}

describe('Profiling date-range labels', () => {
  it('finds the controller that defines them', () => {
    expect(() => readFileSync(PROFILING_CONTROLLER, 'utf8')).not.toThrow();
    expect(profilingRangeKeys().length).toBeGreaterThanOrEqual(10);
  });

  it('translates every range the server can send', () => {
    const missing = profilingRangeKeys()
      .map((key) => `admin.profiling.range.${key}`)
      .filter((catalogKey) => !(catalogKey in EN_CATALOG));

    expect(
      missing,
      'These profiling ranges are defined in ProfilingStatusAPIController but have no catalog\n' +
        'entry, so the page falls back to the server\'s English in every language. Add them to\n' +
        'src/i18n/catalog/{en,es}/admin.ts:\n  ' +
        missing.join('\n  '),
    ).toEqual([]);
  });

  it('has no catalog entry for a range the server no longer sends', () => {
    const known = new Set(profilingRangeKeys());
    const orphans = Object.keys(EN_CATALOG)
      .filter((key) => key.startsWith('admin.profiling.range.'))
      .map((key) => key.slice('admin.profiling.range.'.length))
      .filter((key) => !known.has(key));

    expect(
      orphans,
      'These have catalog entries but are not defined in ProfilingStatusAPIController - either\n' +
        'the range was removed, or its key was renamed and the page is now falling back to English.',
    ).toEqual([]);
  });
});

/**
 * Health summary section labels are authored on the server and rendered both on the Service health
 * page and the Overview snapshot.
 */
const HEALTH_SECTION_FILES = [
  join(process.cwd(), '..', '..', 'Models', 'Health', 'HealthDataSectionRules.cs'),
  join(process.cwd(), '..', '..', 'Models', 'Health', 'HealthService.cs'),
];
const HEALTH_SECTION = /new\s+SectionStatus\s*\{\s*Key\s*=\s*"([^"]+)"\s*,\s*Label\s*=\s*"([^"]+)"/g;

function healthSectionKeys(): string[] {
  return [
    ...new Set(
      HEALTH_SECTION_FILES.flatMap((file) => [...readFileSync(file, 'utf8').matchAll(HEALTH_SECTION)].map((m) => m[1])),
    ),
  ];
}

describe('Health section labels', () => {
  it('finds the files that define them', () => {
    for (const file of HEALTH_SECTION_FILES) {
      expect(() => readFileSync(file, 'utf8')).not.toThrow();
    }
    expect(healthSectionKeys().length).toBeGreaterThanOrEqual(5);
  });

  it('translates every section the server can send', () => {
    const missing = healthSectionKeys()
      .map((key) => `health.section.${key}.label`)
      .filter((catalogKey) => !(catalogKey in EN_CATALOG));

    expect(
      missing,
      'These health sections are defined server-side but have no catalog entry, so the page falls\n' +
        "back to the server's English in every language. Add them to\n" +
        'src/i18n/catalog/{en,es}/health.ts:\n  ' +
        missing.join('\n  '),
    ).toEqual([]);
  });

  it('has no catalog entry for a section the server no longer sends', () => {
    const known = new Set(healthSectionKeys());
    const orphans = Object.keys(EN_CATALOG)
      .filter((key) => key.startsWith('health.section.') && key.endsWith('.label'))
      .map((key) => key.slice('health.section.'.length).replace(/\.label$/, ''))
      .filter((key) => !known.has(key));

    expect(
      orphans,
      'These have catalog entries but are not defined in the health summary - either the section\n' +
        'was removed, or its key was renamed and the page is now falling back to English.',
    ).toEqual([]);
  });
});

const HEALTH_SERVICE_COMPONENT = /Component\s*=\s*"([^"]+)"/g;
const HEALTH_TELEMETRY_COMPONENT = /TrackHealthCheck\(\s*HealthComponent\.([A-Za-z0-9_]+)/g;
const HEALTH_COMPONENT_BLOB_CHECKPOINT_FACTORY = join(process.cwd(), '..', '..', '..', 'WebJob.Office365ActivityImporter.Engine', 'ActivityAPI', 'BlobCheckpoint', 'ProcessedBlobStoreFactory.cs');

function healthComponentKeys(): string[] {
  const source = readFileSync(join(process.cwd(), '..', '..', 'Models', 'Health', 'HealthService.cs'), 'utf8');
  const blobCheckpointSource = readFileSync(HEALTH_COMPONENT_BLOB_CHECKPOINT_FACTORY, 'utf8');
  return sortedUnique([
    ...[...source.matchAll(HEALTH_SERVICE_COMPONENT)].map((m) => m[1]),
    ...[...blobCheckpointSource.matchAll(HEALTH_TELEMETRY_COMPONENT)].map((m) => m[1]),
  ]);
}

describe('Health component display names', () => {
  it('translates every concrete component name the server can send today', () => {
    const serverKeys = healthComponentKeys();
    expect(serverKeys).toEqual(['BlobCheckpoint', 'Credential', 'ServiceBus']);

    const missingMapEntries = serverKeys.filter((key) => !(key in HEALTH_COMPONENT_LABEL_KEYS));
    const missingCatalogEntries = serverKeys
      .map((key) => HEALTH_COMPONENT_LABEL_KEYS[key])
      .filter((catalogKey) => !catalogKey || !(catalogKey in EN_CATALOG));

    expect({ missingMapEntries, missingCatalogEntries }).toEqual({ missingMapEntries: [], missingCatalogEntries: [] });
  });

  it('does not carry stale component-name translations', () => {
    const known = new Set(healthComponentKeys());
    const orphans = Object.keys(HEALTH_COMPONENT_LABEL_KEYS).filter((key) => !known.has(key));

    expect(orphans).toEqual([]);
  });
});

/**
 * Teams Explorer availability reasons are still authored by the server for API compatibility, but the
 * SPA intentionally renders catalogued wording from the stable availability flags instead. Keep the
 * two lists in lock-step so a future server-side reason is not silently dropped on Spanish pages.
 */
const TEAMS_AVAILABILITY_MODEL = join(process.cwd(), '..', '..', '..', 'Common', 'Entities', 'TeamsExplorer', 'TeamsExplorerAvailability.cs');
const TEAMS_AVAILABILITY_BAR = join(process.cwd(), 'src', 'components', 'teamsExplorer', 'AvailabilityBar.tsx');
const TEAMS_AVAILABILITY_REASON_CALL = /model\.Reasons\.Add\(/g;

function teamsAvailabilityReasonCount(): number {
  const source = readFileSync(TEAMS_AVAILABILITY_MODEL, 'utf8');
  return [...source.matchAll(TEAMS_AVAILABILITY_REASON_CALL)].length;
}

describe('Teams Explorer availability reasons', () => {
  it('finds the server model that still authors the compatibility reasons', () => {
    expect(() => readFileSync(TEAMS_AVAILABILITY_MODEL, 'utf8')).not.toThrow();
    expect(() => readFileSync(TEAMS_AVAILABILITY_BAR, 'utf8')).not.toThrow();
    expect(teamsAvailabilityReasonCount()).toBe(8);
  });

  it('has the same catalogued SPA reasons as the server-authored availability reasons', () => {
    const prefix = 'teamsExplorer.availability.reason.';
    const renderedReasons = translationKeysIn(readFileSync(TEAMS_AVAILABILITY_BAR, 'utf8'), prefix);
    const cataloguedReasons = catalogKeys(prefix);

    expect(
      { renderedReasons, cataloguedReasons, serverCount: teamsAvailabilityReasonCount() },
      'TeamsExplorerAvailability added or removed a Reasons.Add(...) branch. Mirror the same boolean\n' +
        'condition in AvailabilityBar.tsx and add/remove the matching\n' +
        'teamsExplorer.availability.reason.* catalog entry in en/es.',
    ).toEqual({ renderedReasons: cataloguedReasons, cataloguedReasons, serverCount: renderedReasons.length });
  });

  it('projects the Service Bus prerequisite as a SPA-visible flag', () => {
    const source = readFileSync(TEAMS_AVAILABILITY_MODEL, 'utf8');
    expect(source).toContain('public bool ServiceBusAvailable { get; set; }');
    expect(source).toContain('ServiceBusAvailable = sources.ServiceBus');
  });
});

/**
 * The SPA picks some chart-text variants by matching the server's English against the catalog's.
 *
 * `chartTranslationKey` / `chartWarningText` in `ReportsPage.tsx` cannot key those on the chart id
 * alone, because one chart emits different wording in different states - "no report rows" versus
 * "confirmation failed", a description that changes when a group filter is on. So they compare the
 * text the server sent with the English in the catalog to decide which variant applies.
 *
 * That works, and it rots silently: reword the C# by one character and the comparison stops
 * matching, the variant is never selected, and the chart quietly falls back to English in every
 * language - with the key-coverage checks above still green, because the key is still there.
 *
 * So the wording is pinned too. If this fails, the server's text changed: update the matching
 * English catalog value (and re-read its translations, which now describe the old sentence).
 */
describe('Chart text the SPA matches on', () => {
  /** Catalog key -> the exact English the C# must still produce for the match to fire. */
  const MATCHED: Record<string, RegExp> = {
    'reports.chart.office-apps-department-adoption.description.groupFiltered': /office-apps-department-adoption/,
    'reports.chart.office-apps-copilot-attach.warning.confirmFailed': /office-apps-copilot-attach/,
    'reports.chart.office-apps-copilot-attach.warning.noReportRows': /office-apps-copilot-attach/,
    'reports.chart.usage-active-users.warning': /usage-active-users/,
  };

  // C# builds these by concatenating adjacent literals across lines, so no whole sentence appears
  // verbatim in the source. Joining `"..." + "..."` back together is what makes a prose fragment
  // findable at all.
  const sources = REPORTS_CONTROLLERS.map((file) => readFileSync(file, 'utf8'))
    .join('\n')
    .replace(/"\s*\+\s*"/g, '');

  it.each(Object.keys(MATCHED))('still finds the English behind %s in the controller', (key) => {
    const english = EN_CATALOG[key];
    expect(english, `${key} is not in the catalog`).toBeTruthy();

    // Compare on the literal prose either side of any placeholder: the C# builds the rest with
    // string concatenation and interpolation, so the whole sentence never appears verbatim.
    const fragments = english
      .split(/\{\w+\}/)
      .map((part) => part.trim())
      .filter((part) => part.length > 12);

    expect(fragments.length, `${key} has no fragment long enough to pin`).toBeGreaterThan(0);

    const missing = fragments.filter((fragment) => !sources.includes(fragment));
    expect(
      missing,
      `The server no longer produces this wording, so ReportsPage's match will not fire and the\n` +
        `chart falls back to English in every language. Update ${key} (and its translations):\n  ` +
        missing.join('\n  '),
    ).toEqual([]);
  });
});

/**
 * Copilot Adoption's two time-saved estimates - the licence estimate and the Cowork estimate - still
 * carry server-authored assumption strings for API compatibility and for the Excel report, but the
 * SPA renders catalogued wording from the same numeric facts. Keep the counts in lock-step so a future
 * server-side assumption is not silently missing on Spanish pages.
 *
 * Each estimate has its own server function, its own list in its own model component and its own
 * catalog prefix. The server's sentences are told apart by the local they are written to:
 * `licenceEstimate` in ModelLicenceValue, `estimate` in ModelCoworkValue - so a sentence added to one
 * must be mirrored on the surface that shows that estimate, not the other.
 *
 * The facts are the assumptions IN FORCE - the reader's own figures for the session, or the product
 * defaults - not the raw server options. Wiring a sentence to `options.*` would state the defaults
 * beside hours computed from the reader's figures, which is exactly the wrong-value mismatch the third
 * check below exists to catch.
 */
const COPILOT_ADOPTION_SCORING = join(
  process.cwd(),
  '..',
  '..',
  '..',
  'Common',
  'Entities',
  'CopilotAdoption',
  'CopilotAdoptionScoring.cs',
);
const COPILOT_ADOPTION_SERVER_TEXT_MODULE = join(process.cwd(), 'src', 'components', 'copilotAdoption', 'serverText.ts');

interface TimeSavedAssumptionSpec {
  name: string;
  /** The model component that renders this estimate's assumption list. */
  component: string;
  /** The server's Add() calls for this estimate, told apart by the local they write to. */
  serverCall: RegExp;
  prefix: string;
  requiredFacts: Record<string, string[]>;
}

const TIME_SAVED_ASSUMPTION_SPECS: TimeSavedAssumptionSpec[] = [
  {
    name: 'licence',
    component: join(process.cwd(), 'src', 'components', 'copilotAdoption', 'LicenceTimeSavedModel.tsx'),
    serverCall: /\blicenceEstimate\.Assumptions\.Add\(/g,
    prefix: 'copilotAdoptionTimeSaved.licence.assumption.',
    requiredFacts: {
      saves: ['assumptions.meetingMinutes', 'assumptions.emailMinutes', 'assumptions.documentMinutes'],
      volumes: ['projection.cohortUsers', 'projection.workingDaysPerMonth'],
      chatUsers: [],
      // The cap the list reached - rendered only when it did.
      capped: ['maxCandidates'],
      lowerBound: ['conservativePercent'],
      potential: [],
      notMeasured: [],
      noMoney: [],
    },
  },
  {
    name: 'Cowork',
    component: join(process.cwd(), 'src', 'components', 'copilotAdoption', 'CoworkTimeSavedModel.tsx'),
    serverCall: /\bestimate\.Assumptions\.Add\(/g,
    prefix: 'copilotAdoptionCowork.estimate.assumption.',
    requiredFacts: {
      // Every minutes and share figure IN FORCE - the reader's, or the defaults - never options.*.
      minutes: [
        'assumptions.organiseMeetingsMinutes',
        'assumptions.prepareMeetingsMinutes',
        'assumptions.sendEmailMinutes',
        'assumptions.postInTeamsMinutes',
        'assumptions.createDocumentsMinutes',
        'assumptions.taskMinutes',
      ],
      shares: [
        'assumptions.organiseMeetingsShare',
        'assumptions.prepareMeetingsShare',
        'assumptions.sendEmailShare',
        'assumptions.postInTeamsShare',
        'assumptions.createDocumentsShare',
      ],
      volumes: ['projection.cohortUsers', 'projection.workingDaysPerMonth', 'monthDays'],
      // The tasks already running, and how many people run them - or the sentence saying nobody does.
      observedTasks: ['projection.observedTasks', 'projection.observedUsers'],
      observedNone: [],
      increment: [],
      leftOut: [],
      lowerBound: ['conservativePercent'],
      potential: [],
      notMeasured: [],
      noMoney: [],
    },
  },
];

function serverAssumptionCount(spec: TimeSavedAssumptionSpec): number {
  const source = readFileSync(COPILOT_ADOPTION_SCORING, 'utf8');
  return [...source.matchAll(spec.serverCall)].length;
}

function renderedAssumptionList(spec: TimeSavedAssumptionSpec): string {
  const source = readFileSync(spec.component, 'utf8');
  return source.match(/<ul className=\{styles\.assumptionList\}>([\s\S]*?)<\/ul>/)?.[1] ?? '';
}

function renderedAssumptionKeys(spec: TimeSavedAssumptionSpec): string[] {
  const list = renderedAssumptionList(spec);
  expect(list, `Could not find the ${spec.name} estimate assumption list`).toBeTruthy();

  return sortedUnique(
    [...list.matchAll(/'([^']+)'/g)]
      .map((m) => m[1])
      .filter((key) => key.startsWith(spec.prefix)),
  );
}

function assumptionIds(spec: TimeSavedAssumptionSpec, keys: string[]): string[] {
  return sortedUnique(keys.map((key) => key.slice(spec.prefix.length).replace(/\.(?:one|other)$/, '')));
}

describe.each(TIME_SAVED_ASSUMPTION_SPECS)('Copilot Adoption $name estimate assumptions', (spec) => {
  it('finds the scoring file that still authors the compatibility assumptions', () => {
    expect(() => readFileSync(COPILOT_ADOPTION_SCORING, 'utf8')).not.toThrow();
    expect(serverAssumptionCount(spec)).toBeGreaterThan(0);
    expect(renderedAssumptionKeys(spec).length).toBeGreaterThan(0);
  });

  it('renders the same catalogued SPA assumptions the server still authors for compatibility', () => {
    const renderedIds = assumptionIds(spec, renderedAssumptionKeys(spec));
    const catalogIds = assumptionIds(spec, catalogKeys(spec.prefix));

    expect(
      { renderedIds, catalogIds, serverCount: serverAssumptionCount(spec) },
      `CopilotAdoptionScoring added or removed a ${spec.serverCall.source} call. Mirror the same\n` +
        `assumption in ${spec.component} using ${spec.prefix}* catalog entries in en/es, or\n` +
        'deliberately remove the obsolete SPA bullet. Plural catalog forms count as one assumption.',
    ).toEqual({ renderedIds: catalogIds, catalogIds, serverCount: renderedIds.length });
  });

  it('wires each assumption sentence to the facts that sentence describes', () => {
    const list = renderedAssumptionList(spec);

    const problems = Object.entries(spec.requiredFacts).flatMap(([id, facts]) => {
      const key = `${spec.prefix}${id}`;
      // Plural forms (.one / .other) count as the one sentence they are.
      const keyPattern = new RegExp(`${key.replace(/\./g, '\\.')}(?:\\.(?:one|other))?'`);
      const keyIndex = list.search(keyPattern);
      if (keyIndex < 0) return [`${id}: missing rendered key`];

      const nextItem = list.indexOf('<li ', keyIndex + 1);
      const item = list.slice(keyIndex, nextItem < 0 ? undefined : nextItem);
      return facts.filter((fact) => !item.includes(fact)).map((fact) => `${id}: missing ${fact}`);
    });

    expect(
      problems,
      `The ${spec.name} assumption bullets must use the same facts as the server-authored compatibility\n` +
        'sentences they replace; a count-only guard cannot catch the right sentence fed by the wrong value.',
    ).toEqual([]);
  });
});

const COWORK_TIER_CONST = /public\s+const\s+string\s+(\w+)\s*=\s*"([^"]+)";/g;
const COWORK_TIER_LABEL_ARM = /case\s+CoworkTiers\.(\w+):\s+return\s+"[^"]+";/g;

function coworkTierKeys(): string[] {
  const source = readFileSync(COPILOT_ADOPTION_SCORING, 'utf8');
  const constants = new Map([...source.matchAll(COWORK_TIER_CONST)].map((m) => [m[1], m[2]]));
  return [...source.matchAll(COWORK_TIER_LABEL_ARM)].map((m) => constants.get(m[1]) ?? m[1]);
}

describe('Copilot Adoption Cowork tier labels and descriptions', () => {
  it('translates every tier label and description the server can send', () => {
    const missing = coworkTierKeys().flatMap((tierKey) =>
      (['label', 'description'] as const)
        .map((field) => `copilotAdoptionCowork.tier.${tierKey}.${field}`)
        .filter((catalogKey) => !(catalogKey in EN_CATALOG)),
    );

    expect(
      missing,
      'CoworkTierLabel/CoworkTierDescription defines a tier without matching SPA catalog text.\n' +
        'Add copilotAdoptionCowork.tier.<tierKey>.label and .description to en/es.',
    ).toEqual([]);
  });

  it('has no Cowork tier catalog entry for a tier the server no longer sends', () => {
    const known = new Set(coworkTierKeys());
    const orphans = Object.keys(EN_CATALOG)
      .filter((key) => key.startsWith('copilotAdoptionCowork.tier.'))
      .map((key) => key.slice('copilotAdoptionCowork.tier.'.length).replace(/\.(?:label|description)$/, ''))
      .filter((tierKey) => !known.has(tierKey));

    expect(
      [...new Set(orphans)],
      'These Cowork tier catalog entries are not defined by CoworkTierLabel; remove or rename them.',
    ).toEqual([]);
  });
});

/**
 * User data lookup category labels and descriptions are authored in UserDataLookupRules with a
 * stable category key. The SPA translates by that key and falls back to the server's English for an
 * unknown future category.
 */
const USER_DATA_RULES = join(process.cwd(), '..', '..', 'Models', 'UserDataLookup', 'UserDataLookupRules.cs');
const USER_DATA_CATEGORY = /new\s+UserDataCategoryMeta\s*\{[\s\S]*?Key\s*=\s*([^,]+),[\s\S]*?Label\s*=\s*"([^"]+)"[\s\S]*?Description\s*=\s*"/g;

function userDataCategoryKeys(): string[] {
  const source = readFileSync(USER_DATA_RULES, 'utf8');
  const constants = new Map([...source.matchAll(/public\s+const\s+string\s+(Cat\w+)\s*=\s*"([^"]+)";/g)].map((m) => [m[1], m[2]]));
  return [...source.matchAll(USER_DATA_CATEGORY)].map((m) => {
    const keyExpression = m[1].trim();
    const constantName = keyExpression.replace(/^UserDataLookupRules\./, '');
    return constants.get(constantName) ?? keyExpression.replace(/^"|"$/g, '');
  });
}

describe('User data lookup category labels', () => {
  it('finds the rules file that defines them', () => {
    expect(() => readFileSync(USER_DATA_RULES, 'utf8')).not.toThrow();
    expect(userDataCategoryKeys().length).toBeGreaterThanOrEqual(25);
  });

  it('translates every category label and description the server can send', () => {
    const missing = userDataCategoryKeys().flatMap((key) =>
      (['label', 'description'] as const)
        .map((field) => `admin.userLookup.category.${key}.${field}`)
        .filter((catalogKey) => !(catalogKey in EN_CATALOG)),
    );

    expect(missing, 'UserDataLookupRules category text is server-authored; add missing admin catalog keys.').toEqual([]);
  });

  it('has no catalog entry for a category the server no longer sends', () => {
    const known = new Set(userDataCategoryKeys());
    const orphans = Object.keys(EN_CATALOG)
      .filter((key) => key.startsWith('admin.userLookup.category.'))
      .map((key) => key.slice('admin.userLookup.category.'.length).replace(/\.(?:label|description)$/, ''))
      .filter((key) => !known.has(key));

    expect([...new Set(orphans)], 'These user lookup category catalog entries are not in UserDataLookupRules.').toEqual([]);
  });
});

/** DLP availability reasons are still authored by DlpAPIController; the SPA mirrors the same flags. */
const DLP_CONTROLLER = join(process.cwd(), '..', '..', 'Controllers', 'DlpAPIController.cs');
const DLP_PAGE = join(process.cwd(), 'src', 'pages', 'DlpPage.tsx');
const DLP_REASON_CALL = /model\.Reasons\.Add\(/g;

describe('DLP availability reasons', () => {
  it('finds the controller that defines them', () => {
    expect(() => readFileSync(DLP_CONTROLLER, 'utf8')).not.toThrow();
    expect(() => readFileSync(DLP_PAGE, 'utf8')).not.toThrow();
    expect([...readFileSync(DLP_CONTROLLER, 'utf8').matchAll(DLP_REASON_CALL)]).toHaveLength(2);
  });

  it('has the same catalogued SPA reasons as the server-authored DLP availability reasons', () => {
    const prefix = 'dlp.availability.reason.';
    const renderedReasons = translationKeysIn(readFileSync(DLP_PAGE, 'utf8'), prefix);
    const cataloguedReasons = catalogKeys(prefix);
    const serverCount = [...readFileSync(DLP_CONTROLLER, 'utf8').matchAll(DLP_REASON_CALL)].length;

    expect(
      { renderedReasons, cataloguedReasons, serverCount },
      'DlpAPIController added or removed a Reasons.Add(...) branch. Mirror the same boolean\n' +
        'condition in DlpPage.tsx and add/remove the matching dlp.availability.reason.* catalog entry.',
    ).toEqual({ renderedReasons: cataloguedReasons, cataloguedReasons, serverCount: renderedReasons.length });
  });
});

const LICENCE_RULES = join(process.cwd(), '..', '..', '..', 'Common', 'Entities', 'LicenceActivity', 'ILicenceActivityStore.cs');
const LICENCE_CONTROLLER = join(process.cwd(), '..', '..', 'Controllers', 'LicenceActivityAPIController.cs');

function csharpStringLiteralValue(value: string): string {
  return value.replace(/\\"/g, '"').replace(/\\r/g, '\r').replace(/\\n/g, '\n');
}

function csharpConstString(source: string, name: string): string {
  const body = new RegExp(`public const string ${name}\\s*=([\\s\\S]*?);\\s*(?:public|static|private|protected|internal)`).exec(source)?.[1] ?? '';
  return [...body.matchAll(/"((?:[^"\\]|\\.)*)"/g)].map((m) => csharpStringLiteralValue(m[1])).join('');
}

function licenceServerNotes(): string[] {
  const rules = readFileSync(LICENCE_RULES, 'utf8');
  const controller = readFileSync(LICENCE_CONTROLLER, 'utf8');
  return [
    /result\.Messages\.Add\("((?:[^"\\]|\\.)*)"\)/.exec(controller)?.[1] ?? '',
    ...[...controller.matchAll(/result\.Messages\.Add\("((?:[^"\\]|\\.)*)"\)/g)].slice(1).map((m) => m[1]),
    csharpConstString(rules, 'AssignmentCaveat'),
    csharpConstString(rules, 'InterpretationCaveat'),
    csharpConstString(rules, 'Method'),
  ].map(csharpStringLiteralValue);
}

/**
 * A `public const string` whose initialiser is ONLY string literals joined by `+`. Anything else - an
 * interpolation, a call, a verbatim string - fails the match, so the gate fails loudly rather than
 * extracting half a sentence.
 */
function csharpConcatenatedConst(source: string, name: string): string {
  const match = new RegExp(`public const string ${name}\\s*=\\s*((?:"(?:[^"\\\\]|\\\\.)*"\\s*\\+?\\s*)+);`).exec(source);
  expect(match, `public const string ${name} = "..." + "..."; not found`).toBeTruthy();
  return [...match![1].matchAll(/"((?:[^"\\]|\\.)*)"/g)].map((m) => csharpStringLiteralValue(m[1])).join('');
}

/** LicenceActivityRules.Notes: the overview and users drill-down notes, in declaration order. */
const LICENCE_RULE_NOTES: Record<string, string> = {
  NoLicences: 'licenceActivity.note.noLicences',
  NobodyHoldsALicence: 'licenceActivity.note.nobodyHoldsALicence',
  NoDisplayNames: 'licenceActivity.note.noDisplayNames',
  DemographicsCapped: 'licenceActivity.note.demographicsCapped',
  UsageReportsGroupFiltered: 'licenceActivity.note.usageReportsGroupFiltered',
  RankingMethod: 'licenceActivity.note.rankingMethod',
  NobodyRankable: 'licenceActivity.note.nobodyRankable',
};

function licenceRulesNotesClass(): string {
  const rules = readFileSync(LICENCE_RULES, 'utf8');
  const start = rules.indexOf('public static class Notes');
  const end = rules.indexOf('public static string ForService', start);
  expect(start, 'LicenceActivityRules.Notes not found').toBeGreaterThanOrEqual(0);
  expect(end, 'LicenceActivityRules.Notes.ForService not found').toBeGreaterThan(start);
  return rules.slice(start, rules.indexOf('\n        }', end));
}

describe('Licence Activity server-authored notes', () => {
  it('maps each server note the page recognises to the exact English catalog text', async () => {
    const { LICENCE_ACTIVITY_NOTE_KEYS } = await import('../../components/licenceActivity/serverNotes');
    const expectedKeys = [
      'licenceActivity.note.userMetadataRequired',
      'licenceActivity.note.privacy',
      'licenceActivity.note.assignmentCaveat',
      'licenceActivity.note.interpretationCaveat',
      'licenceActivity.note.activityMethod',
    ];
    const serverNotes = licenceServerNotes();
    const catalogValuesForNotes = expectedKeys.map((key) => EN_CATALOG[key]);

    expect(serverNotes).toEqual(catalogValuesForNotes);
    expect(LICENCE_ACTIVITY_NOTE_KEYS.slice(0, expectedKeys.length)).toEqual(expectedKeys);
  });

  it('recognises every LicenceActivityRules.Notes sentence by its exact English catalog text', async () => {
    const { LICENCE_ACTIVITY_NOTE_KEYS } = await import('../../components/licenceActivity/serverNotes');
    const notesClass = licenceRulesNotesClass();
    const declared = [...notesClass.matchAll(/public const string (\w+)\s*=/g)].map((m) => m[1]);

    expect(declared, 'A note was added to or removed from LicenceActivityRules.Notes: map it in LICENCE_RULE_NOTES,\n'
      + 'serverNotes.ts and both catalogs, or the Spanish page shows it in English.').toEqual(Object.keys(LICENCE_RULE_NOTES));

    for (const [name, key] of Object.entries(LICENCE_RULE_NOTES)) {
      expect(EN_CATALOG[key], `${key} must be LicenceActivityRules.Notes.${name} verbatim`).toBe(csharpConcatenatedConst(notesClass, name));
      expect(LICENCE_ACTIVITY_NOTE_KEYS, `serverNotes.ts must recognise ${key}`).toContain(key);
    }
  });

  it('rebuilds the per-service coverage note from the same shape the server writes', () => {
    const notesClass = licenceRulesNotesClass();
    expect(notesClass.replace(/\s+/g, ' ')).toContain('return WorkloadLabel(workload) + ": " + message;');
    expect(EN_CATALOG['licenceActivity.note.forService']).toBe('{service}: {message}');

    const rules = readFileSync(LICENCE_RULES, 'utf8');
    const labelStart = rules.indexOf('public static string WorkloadLabel');
    expect(labelStart, 'LicenceActivityRules.WorkloadLabel not found').toBeGreaterThanOrEqual(0);
    const labelSwitch = rules.slice(labelStart, rules.indexOf('default: return workload;', labelStart));
    const serverLabels = Object.fromEntries([...labelSwitch.matchAll(/case "(\w+)": return "([^"]+)";/g)].map((m) => [m[1], m[2]]));
    expect(Object.keys(serverLabels).length, 'WorkloadLabel cases not found').toBe(5);
    expect(Object.fromEntries(WORKLOADS.map((w) => [w.key, w.label])), 'WORKLOADS labels must equal LicenceActivityRules.WorkloadLabel')
      .toEqual(serverLabels);
  });

  it('names the id 0 demographic bucket in English exactly as the server (and the Excel export) does', () => {
    const readModel = readFileSync(join(process.cwd(), '..', '..', '..', 'Common', 'Entities', 'LicenceActivity', 'LicenceActivityReadModel.cs'), 'utf8');
    const serverName = /if \(id == 0\) name = "([^"]+)";/.exec(readModel)?.[1];
    expect(serverName, 'LicenceActivityReadModel no longer names the id 0 bucket where this gate looks').toBeTruthy();
    expect(EN_CATALOG['licenceActivity.demographics.unknownBucket']).toBe(serverName);
  });
});

/** Agent cost availability messages are server-authored in SqlAgentCostReportStore.AddMessages. */
const AGENT_COST_STORE = join(process.cwd(), '..', '..', '..', 'Common', 'Entities', 'AgentCosts', 'SqlAgentCostReportStore.cs');
const AGENT_COST_PAGE = join(process.cwd(), 'src', 'pages', 'AgentCostsPage.tsx');
const AGENT_COST_MESSAGE_CALL = /result\.Messages\.Add\(/g;

describe('Agent cost availability messages', () => {
  it('finds the store that defines them', () => {
    expect(() => readFileSync(AGENT_COST_STORE, 'utf8')).not.toThrow();
    expect(() => readFileSync(AGENT_COST_PAGE, 'utf8')).not.toThrow();
    expect([...readFileSync(AGENT_COST_STORE, 'utf8').matchAll(AGENT_COST_MESSAGE_CALL)].length).toBeGreaterThanOrEqual(10);
  });

  it('has the same catalogued SPA messages as the server-authored availability messages', () => {
    const prefix = 'agentCosts.availability.message.';
    const renderedMessages = translationKeysIn(readFileSync(AGENT_COST_PAGE, 'utf8'), prefix);
    const cataloguedMessages = catalogKeys(prefix);
    const serverCount = [...readFileSync(AGENT_COST_STORE, 'utf8').matchAll(AGENT_COST_MESSAGE_CALL)].length;

    expect(
      { renderedMessages, cataloguedMessages, serverCount },
      'SqlAgentCostReportStore.AddMessages added or removed a result.Messages.Add(...) branch.\n' +
        'Mirror the same condition in AgentCostsPage.tsx and add/remove the matching\n' +
        'agentCosts.availability.message.* catalog entry.',
    ).toEqual({ renderedMessages: cataloguedMessages, cataloguedMessages, serverCount: renderedMessages.length });
  });
});

/** Web activity distribution bucket labels are authored in WebActivityScoring/SqlWebActivityStore. */
const WEB_ACTIVITY_SCORING = join(process.cwd(), '..', '..', '..', 'Common', 'Entities', 'SpoWebActivity', 'WebActivityScoring.cs');

function webActivityBucketCatalogKeys(): string[] {
  const source = readFileSync(WEB_ACTIVITY_SCORING, 'utf8');
  const visitorSegments = [...source.matchAll(/new\[\]\s*\{\s*"Daily",\s*"Regular",\s*"Occasional",\s*"Rare",\s*"One-off"\s*\}/g)].length
    ? ['Daily', 'Regular', 'Occasional', 'Rare', 'One-off']
    : [];
  const depth = [...source.matchAll(/Tuple\.Create\(\d+L,\s*"([^"]+)"\)/g)].map((m) => m[1]);
  const days = [...source.matchAll(/new\[\]\s*\{\s*"Monday",\s*"Tuesday",\s*"Wednesday",\s*"Thursday",\s*"Friday",\s*"Saturday",\s*"Sunday"\s*\}/g)].length
    ? ['0', '1', '2', '3', '4', '5', '6']
    : [];
  const periods = [...source.matchAll(/new\s+PeriodOfDay\("([^"]+)"/g)].map((m) => m[1]);

  return [
    ...visitorSegments.map((key) => `webActivity.bucket.visitorSegment.${key}.label`),
    ...depth.map((key) => `webActivity.bucket.visitDepth.${key}.label`),
    ...days.map((key) => `webActivity.bucket.day.${key}.label`),
    ...periods.map((key) => `webActivity.bucket.period.${key}.label`),
  ];
}

describe('Web activity bucket labels', () => {
  it('finds the scoring file that defines them', () => {
    expect(() => readFileSync(WEB_ACTIVITY_SCORING, 'utf8')).not.toThrow();
    expect(webActivityBucketCatalogKeys().length).toBeGreaterThanOrEqual(20);
  });

  it('translates every bucket label the server can send', () => {
    const missing = webActivityBucketCatalogKeys().filter((catalogKey) => !(catalogKey in EN_CATALOG));
    expect(missing, 'These web activity bucket labels are server-authored; add missing webActivity catalog keys.').toEqual([]);
  });

  it('has no catalog entry for a bucket the server no longer sends', () => {
    const expected = new Set(webActivityBucketCatalogKeys());
    const orphans = Object.keys(EN_CATALOG)
      .filter((key) => key.startsWith('webActivity.bucket.'))
      .filter((key) => !expected.has(key));
    expect(orphans, 'These web activity bucket catalog entries are not defined by WebActivityScoring.').toEqual([]);
  });
});

/**
 * Copilot Adoption still receives several user-visible labels and rationale branches from the .NET
 * scoring/service layer. The SPA translates those by stable enum/code/fact branches and falls back
 * only where the server is carrying tenant data or diagnostics.
 */
const COPILOT_ADOPTION_SCORING_TEXT = join(
    process.cwd(),
    '..',
    '..',
    '..',
    'Common',
    'Entities',
    'CopilotAdoption',
    'CopilotAdoptionScoring.cs',
  );
const COPILOT_ADOPTION_SERVICE_TEXT = join(
    process.cwd(),
    '..',
    '..',
    '..',
    'Common',
    'Entities',
    'CopilotAdoption',
    'CopilotAdoptionService.cs',
  );
const COPILOT_ADOPTION_CONTROLLER_TEXT = join(process.cwd(), '..', '..', 'Controllers', 'CopilotAdoptionAPIController.cs');
const COPILOT_ADOPTION_OPTIONS_TEXT = join(
    process.cwd(),
    '..',
    '..',
    '..',
    'Common',
    'Entities',
    'CopilotAdoption',
    'CopilotAdoptionOptions.cs',
  );

function copilotAdoptionScoringSource(): string {
    return readFileSync(COPILOT_ADOPTION_SCORING_TEXT, 'utf8');
  }

function copilotAdoptionServiceSource(): string {
    return readFileSync(COPILOT_ADOPTION_SERVICE_TEXT, 'utf8');
  }

function copilotAdoptionServerTextModuleSource(): string {
    return readFileSync(COPILOT_ADOPTION_SERVER_TEXT_MODULE, 'utf8');
  }

function copilotActionCodes(): string[] {
    const source = copilotAdoptionScoringSource();
    const actionClass = source.match(/public static class AdoptionActionCodes\s*\{([\s\S]*?)\n\s*\}/)?.[1] ?? '';
    expect(actionClass, 'Could not find AdoptionActionCodes').toBeTruthy();

    const codes = sortedUnique([...actionClass.matchAll(/public const string \w+ = "([^"]+)";/g)].map((m) => m[1]));
    expect(codes.length, 'AdoptionActionCodes extraction matched no codes').toBeGreaterThanOrEqual(9);
    return codes;
  }

function copilotAdoptionCatalogCodes(prefix: string): string[] {
    return sortedUnique(catalogKeys(prefix).map((key) => key.slice(prefix.length).replace(/\.[^.]+$/, '')));
  }

function copilotFunnelLabels(): string[] {
    const source = copilotAdoptionServiceSource();
    const block = source.match(/private static List<AdoptionCategory> BuildFunnel\([\s\S]*?return new List<AdoptionCategory>\s*\{([\s\S]*?)\n\s*\};/)?.[1] ?? '';
    expect(block, 'Could not find BuildFunnel labels').toBeTruthy();
    return [...block.matchAll(/new AdoptionCategory \{ Label = "([^"]+)"/g)].map((m) => m[1]);
  }

function copilotScoreProfileLabels(): string[] {
    return [...copilotAdoptionServiceSource().matchAll(/profiles\.Add\(Profile\("([^"]+)"/g)].map((m) => m[1]);
  }

function copilotConcentrationLabels(): string[] {
    return [...copilotAdoptionScoringSource().matchAll(/Tuple\.Create\("([^"]+)",\s*0\.\d+\)/g)].map((m) => m[1]);
  }

describe('Copilot Adoption server-authored text', () => {
    it('finds the C# files that define it', () => {
      expect(() => readFileSync(COPILOT_ADOPTION_SCORING_TEXT, 'utf8')).not.toThrow();
      expect(() => readFileSync(COPILOT_ADOPTION_SERVICE_TEXT, 'utf8')).not.toThrow();
      expect(() => readFileSync(COPILOT_ADOPTION_CONTROLLER_TEXT, 'utf8')).not.toThrow();
      expect(copilotAdoptionScoringSource()).toContain('public static string ActionLabel');
      expect(copilotAdoptionServiceSource()).toContain('BuildFunnel');
    });

    it('translates every recommended-action code the scorer can send', () => {
      const codes = copilotActionCodes();
      const labelCodes = copilotAdoptionCatalogCodes('copilotAdoption.server.action.').filter((code) =>
        catalogKeys(`copilotAdoption.server.action.${code}.`).includes(`copilotAdoption.server.action.${code}.label`));
      const descriptionCodes = copilotAdoptionCatalogCodes('copilotAdoption.server.action.').filter((code) =>
        catalogKeys(`copilotAdoption.server.action.${code}.`).includes(`copilotAdoption.server.action.${code}.description`));

      const missing = codes.flatMap((code) => [
          `copilotAdoption.server.action.${code}.label`,
          `copilotAdoption.server.action.${code}.description`,
        ]).filter((key) => !(key in EN_CATALOG));

      expect(
        { missing, labelCodes, descriptionCodes },
        'AdoptionActionCodes and copilotAdoption.server.action.* catalog entries must be an exact\n' +
          'two-way match. A new C# action code with no catalog entry would otherwise fall back to English.',
      ).toEqual({ missing: [], labelCodes: codes, descriptionCodes: codes });
    });

    it('translates every accountability dimension and empty bucket the service can send', () => {
      // Read from the definitions, not from a list of the ones that exist today: a sixth dimension
      // or empty bucket added to the C# must fail here until the SPA can translate it.
      const optionsSource = readFileSync(COPILOT_ADOPTION_OPTIONS_TEXT, 'utf8');
      const dimensionClass = optionsSource.match(/public static class CopilotAdoptionAccountabilityDimensions\s*\{([\s\S]*?)\n\s*\}/)?.[1] ?? '';
      expect(dimensionClass, 'Could not find CopilotAdoptionAccountabilityDimensions').toBeTruthy();
      const dimensions = sortedUnique([...dimensionClass.matchAll(/public const string \w+ = "([^"]+)";/g)].map((m) => m[1]));
      const service = copilotAdoptionServiceSource();
      // Scoped to the method that assigns them, so an unrelated "noSomething" literal elsewhere in the
      // service cannot fail this for the wrong reason.
      const emptyKeyMethod = service.match(/static string EmptyAccountabilitySegmentKey\([\s\S]*?\n {8}\}/)?.[0] ?? '';
      expect(emptyKeyMethod, 'Could not find EmptyAccountabilitySegmentKey').toBeTruthy();
      const emptyKeys = sortedUnique([...emptyKeyMethod.matchAll(/"(no[A-Z]\w*)"/g)].map((m) => m[1]));
      expect(dimensions.length, 'accountability dimension extraction matched nothing').toBeGreaterThanOrEqual(5);
      expect(emptyKeys.length, 'empty-bucket key extraction matched nothing').toBeGreaterThanOrEqual(5);

      expect(Object.keys(ACCOUNTABILITY_DIMENSION_TEXT).sort()).toEqual(dimensions.sort());
      expect(Object.values(ACCOUNTABILITY_DIMENSION_TEXT).flatMap((entry) => [
        entry.labelKey,
        entry.aggregateViewKey,
        entry.safeViewKey,
      ]).filter((key) => !(key in EN_CATALOG))).toEqual([]);
      expect(Object.keys(ACCOUNTABILITY_EMPTY_SEGMENT_KEYS).sort()).toEqual(emptyKeys.sort());
      expect(Object.values(ACCOUNTABILITY_EMPTY_SEGMENT_KEYS).filter((key) => !(key in EN_CATALOG))).toEqual([]);
    });

    it('translates every engagement band and habit bucket label the scorer can send', () => {
      const source = copilotAdoptionScoringSource();
      const bandLabels = sortedUnique([...source.matchAll(/case AdoptionBand\.\w+: return "([^"]+)";/g)].map((m) => m[1]));
      const bucketLabels = sortedUnique([...source.matchAll(/"((?:Infrequent|Moderate|Frequent|Daily))"/g)].map((m) => m[1]));

      expect(bandLabels.length).toBeGreaterThanOrEqual(6);
      expect(bucketLabels.length).toBe(4);

      const serverText = copilotAdoptionServerTextModuleSource();
      expectServerLabelsCoveredBySpaMap(
        bandLabels,
        labelKeyMapFromFunction(serverText, 'adoptionBandLabel', 'copilotAdoption.server.band.'),
        'Copilot Adoption band labels',
      );
      expectServerLabelsCoveredBySpaMap(
        bucketLabels,
        labelKeyMapFromFunction(serverText, 'habitBucketLabel', 'copilotAdoption.server.habitBucket.'),
        'Copilot Adoption habit bucket labels',
      );

      const rangeKeys = catalogKeys('copilotAdoption.server.habitBucket.').filter((key) => key.endsWith('.range'));
      expect(rangeKeys.map((key) => key.replace(/\.range$/, ''))).toEqual(catalogKeys('copilotAdoption.server.habitBucket.').filter((key) => !key.endsWith('.range')));
    });

    it('translates every funnel, concentration and profile label the service can send', () => {
      const serverText = copilotAdoptionServerTextModuleSource();
      expectServerLabelsCoveredBySpaMap(
        copilotFunnelLabels(),
        labelKeyMapFromFunction(serverText, 'funnelStageLabel', 'copilotAdoption.server.funnel.'),
        'Copilot Adoption funnel labels',
      );
      expectServerLabelsCoveredBySpaMap(
        copilotScoreProfileLabels(),
        labelKeyMapFromFunction(serverText, 'scoreProfileLabel', 'copilotAdoption.server.scoreProfile.'),
        'Copilot Adoption score profile labels',
      );
      expectServerLabelsCoveredBySpaMap(
        copilotConcentrationLabels(),
        labelKeyMapFromFunction(serverText, 'concentrationLabel', 'copilotAdoption.server.concentration.'),
        'Copilot Adoption concentration labels',
      );
    });

    it('translates every agent health and opportunity tier branch the scorer can send', () => {
      const source = copilotAdoptionScoringSource();
      expect(source).toContain('AgentHealthDisplayName');
      expect(source).toContain('OpportunityTierLabel');

      const expected = [
        'copilotAdoptionAgents.server.healthReason.new',
        'copilotAdoptionAgents.server.healthReason.retire.withDays',
        'copilotAdoptionAgents.server.healthReason.retire.noUse',
        'copilotAdoptionAgents.server.healthReason.review.quiet',
        'copilotAdoptionAgents.server.healthReason.review.fewUsers',
        'copilotAdoptionAgents.server.healthReason.keep',
        'copilotAdoptionUsers.server.opportunityTier.provenDemand',
        'copilotAdoptionUsers.server.opportunityTier.workloadInferred',
        'copilotAdoptionUsers.server.opportunityTier.none',
      ];

      expect(expected.filter((key) => !(key in EN_CATALOG))).toEqual([]);
    });
  });

/**
 * Enabled-import badges on the Overview and Service configuration pages come from
 * HealthService.ImportLabelsBySettingProperty. The API still sends the English label rather than
 * the stable setting-property key, so the SPA map records both: the key for drift detection and
 * the English label for current rendering.
 */
const HEALTH_SERVICE = join(process.cwd(), '..', '..', 'Models', 'Health', 'HealthService.cs');
const IMPORT_LABEL_ENTRY = /\{\s*nameof\(ImportTaskSettings\.(\w+)\),\s*"([^"]+)"\s*\}/g;

function enabledImportLabelsBySettingProperty(): Record<string, string> {
  const source = readFileSync(HEALTH_SERVICE, 'utf8');
  return Object.fromEntries([...source.matchAll(IMPORT_LABEL_ENTRY)].map((m) => [m[1], m[2]]));
}

describe('Enabled-import badges', () => {
  it('finds the health service map that defines them', () => {
    expect(() => readFileSync(HEALTH_SERVICE, 'utf8')).not.toThrow();
    expect(Object.keys(enabledImportLabelsBySettingProperty()).length).toBeGreaterThanOrEqual(10);
  });

  it('has a SPA label entry for every server setting-property key', () => {
    const server = enabledImportLabelsBySettingProperty();
    const spa = ENABLED_IMPORT_LABELS_BY_SETTING_PROPERTY;

    expect(Object.keys(spa).sort()).toEqual(Object.keys(server).sort());
    expect(Object.fromEntries(Object.entries(spa).map(([key, value]) => [key, value.english]))).toEqual(server);
    expect(Object.values(spa).map((value) => value.key).filter((key) => !(key in EN_CATALOG))).toEqual([]);
  });
});

/**
 * User Lookup workload badges and source lists are server-authored product wording. Categories
 * already have their own guard above; this covers the workload catalogue beside them.
 */
const USER_DATA_WORKLOAD = /new\s+UserDataWorkloadDef\s*\{\s*Flag\s*=\s*Wf\.(\w+),\s*Name\s*=\s*"([^"]+)",\s*Description\s*=\s*"([^"]+)"/g;

function userDataWorkloadsByFlag(): Record<string, { name: string; description: string }> {
  const source = readFileSync(USER_DATA_RULES, 'utf8');
  const flags = new Map([...source.matchAll(/public\s+const\s+string\s+(\w+)\s*=\s*"([^"]+)";/g)].map((m) => [m[1], m[2]]));
  return Object.fromEntries([...source.matchAll(USER_DATA_WORKLOAD)].map((m) => [
    flags.get(m[1]) ?? m[1],
    { name: m[2], description: m[3] },
  ]));
}

describe('User data lookup workload labels', () => {
  it('finds the rules file that defines them', () => {
    expect(() => readFileSync(USER_DATA_RULES, 'utf8')).not.toThrow();
    expect(Object.keys(userDataWorkloadsByFlag()).length).toBeGreaterThanOrEqual(8);
  });

  it('has a SPA label entry for every server workload flag', () => {
    const server = userDataWorkloadsByFlag();
    const spa = USER_DATA_WORKLOADS_BY_FLAG;

    expect(Object.keys(spa).sort()).toEqual(Object.keys(server).sort());
    expect(Object.fromEntries(Object.entries(spa).map(([key, value]) => [key, value.english]))).toEqual(
      Object.fromEntries(Object.entries(server).map(([key, value]) => [key, value.name])),
    );
    expect(Object.values(spa).flatMap((value) => [value.nameKey, value.descriptionKey]).filter((key) => !(key in EN_CATALOG))).toEqual([]);
  });
});

function teamsSegmentKeysFromScoring(): string[] {
  const source = readFileSync(TEAMS_SCORING, 'utf8');
  return sortedUnique([...source.matchAll(/case TeamsUserSegment\.(\w+): return "[^"]*";/g)].map((m) => m[1]));
}

function teamsMeetingBucketKeys(): Record<keyof typeof TEAMS_MEETING_BUCKET_LABEL_KEYS, string[]> {
  const source = readFileSync(join(process.cwd(), '..', '..', '..', 'Common', 'Entities', 'TeamsExplorer', 'SqlTeamsExplorerStore.cs'), 'utf8');
  const sizeBlock = source.match(/private static List<TeamsBucketRow> BuildSizeBuckets[\s\S]*?var buckets = new\[\]\s*\{([\s\S]*?)\};/)?.[1] ?? '';
  const durationBlock = source.match(/private static List<TeamsBucketRow> BuildDurationBuckets[\s\S]*?var buckets = new\[\]\s*\{([\s\S]*?)\};/)?.[1] ?? '';
  const periodBlock = source.match(/private static List<TeamsBucketRow> BuildPeriodOfDay[\s\S]*?var buckets = new\[\]\s*\{([\s\S]*?)\};/)?.[1] ?? '';

  return {
    size: sortedUnique([...sizeBlock.matchAll(/Key = "([^"]+)"/g)].map((m) => m[1])),
    duration: sortedUnique([...durationBlock.matchAll(/Key = "([^"]+)"/g)].map((m) => m[1])),
    period: sortedUnique([...periodBlock.matchAll(/Key = "([^"]+)"/g)].map((m) => m[1])),
  };
}

describe('Teams Explorer segment and meeting bucket labels', () => {
  it('finds the C# that defines them', () => {
    expect(() => readFileSync(TEAMS_SCORING, 'utf8')).not.toThrow();
    expect(teamsSegmentKeysFromScoring().length).toBeGreaterThanOrEqual(4);
    expect(teamsMeetingBucketKeys().size.length).toBeGreaterThanOrEqual(7);
  });

  it('has a SPA label entry for every server segment', () => {
    expect(Object.keys(TEAMS_SEGMENT_TEXT_KEYS).sort()).toEqual(teamsSegmentKeysFromScoring());
    expect(Object.values(TEAMS_SEGMENT_TEXT_KEYS).flatMap((value) => [value.labelKey, value.descriptionKey]).filter((key) => !(key in EN_CATALOG))).toEqual([]);
  });

  it('has a SPA label entry for every server meeting bucket key', () => {
    const server = teamsMeetingBucketKeys();
    for (const group of Object.keys(TEAMS_MEETING_BUCKET_LABEL_KEYS) as (keyof typeof TEAMS_MEETING_BUCKET_LABEL_KEYS)[]) {
      expect(sortedUnique(Object.keys(TEAMS_MEETING_BUCKET_LABEL_KEYS[group]))).toEqual(server[group]);
      expect(Object.values(TEAMS_MEETING_BUCKET_LABEL_KEYS[group]).filter((key) => !(key in EN_CATALOG))).toEqual([]);
    }
  });
});

/**
 * Web Activity availability reasons are still serialized as English for API compatibility; the SPA
 * renders catalogued wording from the same availability facts. One legacy branch ("configuration
 * unreadable") is not distinguishable from the booleans on the wire, so the SPA map includes a
 * compatibility key until the API exposes that state explicitly.
 */
const WEB_ACTIVITY_AVAILABILITY = join(process.cwd(), '..', '..', '..', 'Common', 'Entities', 'SpoWebActivity', 'WebActivityAvailability.cs');
const WEB_ACTIVITY_REASON_CALL = /model\.Reasons\.Add\(/g;

describe('Web Activity availability reasons', () => {
  it('finds the server model that still authors the compatibility reasons', () => {
    expect(() => readFileSync(WEB_ACTIVITY_AVAILABILITY, 'utf8')).not.toThrow();
    expect([...readFileSync(WEB_ACTIVITY_AVAILABILITY, 'utf8').matchAll(WEB_ACTIVITY_REASON_CALL)].length).toBe(8);
  });

  const BLOB_CHECKPOINT_FACTORY = join(process.cwd(), '..', '..', '..', 'WebJob.Office365ActivityImporter.Engine', 'ActivityAPI', 'BlobCheckpoint', 'ProcessedBlobStoreFactory.cs');
  const BLOB_CHECKPOINT_REASON_KEY = /new\s+BlobCheckpointFailureClassification\(\s*"([^"]+)"/g;
  const BLOB_CHECKPOINT_HEALTH_REASON_KEY = /TrackHealthCheck\(\s*HealthComponent\.BlobCheckpoint[\s\S]*?reasonKey:\s*"([^"]+)"/g;

  function blobCheckpointReasonKeys(): string[] {
    const source = readFileSync(BLOB_CHECKPOINT_FACTORY, 'utf8');
    return sortedUnique([
      ...[...source.matchAll(BLOB_CHECKPOINT_REASON_KEY)].map((m) => m[1]),
      ...[...source.matchAll(BLOB_CHECKPOINT_HEALTH_REASON_KEY)].map((m) => m[1]),
    ]);
  }

  describe('Blob checkpoint component health reasons', () => {
    it('finds the importer factory that emits them', () => {
      expect(() => readFileSync(BLOB_CHECKPOINT_FACTORY, 'utf8')).not.toThrow();
      expect(blobCheckpointReasonKeys()).toEqual([
        'blobCheckpoint.authenticationFailed',
        'blobCheckpoint.healthy',
        'blobCheckpoint.keyAuthDisabled',
        'blobCheckpoint.notConfigured',
        'blobCheckpoint.permissionMismatch',
        'blobCheckpoint.storageFirewall',
        'blobCheckpoint.storageRejected',
        'blobCheckpoint.transport',
      ]);
    });

    it('maps every stable reason key the server emits to a catalog entry', () => {
      const serverKeys = blobCheckpointReasonKeys();
      const mappedKeys = sortedUnique(Object.keys(BLOB_CHECKPOINT_REASON_KEYS));
      const mappedCatalogKeys = Object.values(BLOB_CHECKPOINT_REASON_KEYS).filter((key) => !(key in EN_CATALOG));

      expect({
        missing: serverKeys.filter((key) => !mappedKeys.includes(key)),
        orphaned: mappedKeys.filter((key) => !serverKeys.includes(key)),
        missingCatalog: mappedCatalogKeys,
      }).toEqual({ missing: [], orphaned: [], missingCatalog: [] });
    });

    it('keeps the legacy storage-firewall detail recognisable until old telemetry ages out', () => {
      const source = readFileSync(BLOB_CHECKPOINT_FACTORY, 'utf8');
      const requiredFragments = [
        'Azure Table checkpoint unavailable:',
        'Storage firewall/network rules rejected the Table checkpoint request',
        "set the storage account to 'Enabled from all networks'",
        'Using non-durable in-memory checkpoint',
        'See importer error log.',
      ];

      for (const fragment of requiredFragments) {
        expect(source).toContain(fragment);
      }
    });
  });

  it('has catalogued SPA reasons for the server-authored availability branches', () => {
    expect(Object.values(WEB_ACTIVITY_AVAILABILITY_REASON_KEYS).filter((key) => !(key in EN_CATALOG))).toEqual([]);
    expect(Object.keys(WEB_ACTIVITY_AVAILABILITY_REASON_KEYS)).toEqual([
      'configurationUnreadable',
      'webTrafficOffWithExistingHits',
      'webTrafficOffNoHits',
      'appInsightsMissing',
      'noPageViewsKnown',
      'pageViewCheckFailed',
      'staleCollection',
      'userMetadataOff',
      'noSearches',
      'noClicks',
    ]);
  });
});

/**
 * Health's Overview and section roll-ups are sentences HealthRollup writes in C#, and the component
 * details are sentences HealthService and the importer write. The API sends no key beside them, so
 * the SPA translates by recognising the English - and a sentence it does not recognise reaches a
 * Spanish reader in English with every other check green, because from the SPA's side there is no
 * string and no missing key.
 *
 * So these tests rebuild each sentence from the C# itself, fill its placeholders with sample values
 * and run it through the SPA's translator in Spanish. A new or reworded server sentence fails here
 * rather than on a customer's Health page.
 */
const HEALTH_ROLLUP = join(process.cwd(), '..', '..', '..', 'Common', 'DataUtils', 'Health', 'HealthRollup.cs');
const HEALTH_DATA_SECTION_RULES = join(process.cwd(), '..', '..', 'Models', 'Health', 'HealthDataSectionRules.cs');
const HEALTH_BLOB_CHECKPOINT_FACTORY = join(process.cwd(), '..', '..', '..', 'WebJob.Office365ActivityImporter.Engine', 'ActivityAPI', 'BlobCheckpoint', 'ProcessedBlobStoreFactory.cs');

/** `"..."`, `$"..."` or `"..." + x`: the shapes the Health C# builds its sentences from. */
const CSHARP_SENTENCE = String.raw`(\$?)"((?:[^"\\]|\\.)*)"(\s*\+\s*[\w.]+)?`;

interface CSharpSentence {
  interpolated: boolean;
  text: string;
  concatenated: boolean;
}

function csharpSentences(source: string, around: (sentence: string) => string): CSharpSentence[] {
  return [...source.matchAll(new RegExp(around(CSHARP_SENTENCE), 'g'))].map((m) => ({
    interpolated: m[1] === '$',
    text: m[2],
    concatenated: !!m[3],
  }));
}

/** The sentence the server would send, with each `{hole}` (and a trailing `+ x`) given a sample value. */
function sampleOf(sentence: CSharpSentence, holeValue: (hole: string) => string = () => '7'): string {
  const body = sentence.interpolated ? sentence.text.replace(/\{([^{}]+)\}/g, (_, hole: string) => holeValue(hole)) : sentence.text;
  return sentence.concatenated ? `${body}sample server error` : body;
}

/** The sentence's own English, which must not survive translation into Spanish. */
function englishOf(sentence: CSharpSentence): string[] {
  const parts = sentence.interpolated ? sentence.text.split(/\{[^{}]+\}/) : [sentence.text];
  return parts.map((part) => part.trim()).filter((part) => part.length >= 12);
}

function leaksEnglish(sentence: CSharpSentence, translated: string): boolean {
  return englishOf(sentence).some((fragment) => translated.includes(fragment));
}

describe('Health sentences the SPA recognises', () => {
  const es: TFunction = (key, values) => translateStatic('es', key, values);

  beforeAll(async () => {
    await loadCatalog('es');
  });

  function rollupSentences(): CSharpSentence[] {
    return csharpSentences(
      readFileSync(HEALTH_ROLLUP, 'utf8'),
      (sentence) => String.raw`(?:Raise\(HealthStatus\.\w+,|reasons\.Add\()\s*${sentence}\s*\)`,
    );
  }

  /** HealthRollup only raises the webhook reason for these states, so only they need recognising. */
  function webhookStates(): string[] {
    return sortedUnique([...readFileSync(HEALTH_ROLLUP, 'utf8').matchAll(/string\.Equals\(input\.WebhookState,\s*"(\w+)"/g)].map((m) => m[1]));
  }

  function sectionSentences(): CSharpSentence[] {
    return [HEALTH_SERVICE, HEALTH_DATA_SECTION_RULES].flatMap((file) => {
      const source = readFileSync(file, 'utf8');
      return [
        ...csharpSentences(source, (sentence) => String.raw`Reasons\s*=\s*new List<string>\s*\{\s*${sentence}\s*\}`),
        ...csharpSentences(source, (sentence) => String.raw`RaiseAtLeastDegraded\(\s*section,\s*${sentence}\s*\)`),
      ];
    });
  }

  /** A C# method's body, found by its declaration. Adequate for HealthService, whose braces all balance. */
  function csharpMethodBody(source: string, name: string): string {
    const declaration = new RegExp(String.raw`\b(?:Task|void)\s+${name}\(`).exec(source);
    expect(declaration, `Could not find the declaration of ${name}`).toBeTruthy();
    const start = declaration?.index ?? 0;
    const brace = source.indexOf('{', source.indexOf(')', start));
    let depth = 0;
    for (let i = brace; i < source.length; i++) {
      if (source[i] === '{') depth++;
      if (source[i] === '}') depth--;
      if (depth === 0) return source.slice(brace + 1, i);
    }
    throw new Error(`Could not find the end of ${name}`);
  }

  const COMPONENT_CHECKS = ['LoadCredentialHealth', 'LoadServiceBusHealth'];

  /** Every `Detail = ...` the runtime component checks give a row - string literals may hold `,` and `;`. */
  function componentDetailSentences(): CSharpSentence[] {
    const source = readFileSync(HEALTH_SERVICE, 'utf8');
    const initialiser = /(?<![\w.])Detail\s*=\s*((?:\$?"(?:[^"\\]|\\.)*"|[^",;])+?),\s*\r?\n/g;
    return COMPONENT_CHECKS
      .map((name) => csharpMethodBody(source, name))
      .flatMap((body) => [...body.matchAll(initialiser)].map((m) => m[1]))
      .flatMap((expression) => csharpSentences(expression, (sentence) => sentence));
  }

  /** The hint LoadServiceBusHealth appends to a queue-depth failure that looks like a network block. */
  function queueDepthNetworkHint(): string {
    const body = csharpMethodBody(readFileSync(HEALTH_SERVICE, 'utf8'), 'LoadServiceBusHealth').replace(/"\s*\+\s*"/g, '');
    return /detail \+= "((?:[^"\\]|\\.)*)";/.exec(body)?.[1] ?? '';
  }

  /** The importer's source with `"..." + "..."` joined back into single literals. */
  function blobCheckpointSource(): string {
    return readFileSync(HEALTH_BLOB_CHECKPOINT_FACTORY, 'utf8').replace(/"\s*\+\s*"/g, '');
  }

  function blobCheckpointFailures(): { reasonKey: string; message: string }[] {
    return [...blobCheckpointSource().matchAll(/new\s+BlobCheckpointFailureClassification\(\s*"([^"]+)"[^"]*?\$?"((?:[^"\\]|\\.)*)"\s*\)/g)]
      .map((m) => ({ reasonKey: m[1], message: m[2] }));
  }

  it('finds the C# that writes them', () => {
    expect(rollupSentences().length).toBeGreaterThanOrEqual(11);
    expect(webhookStates()).toEqual(['Error', 'Missing']);
    expect(sectionSentences().length).toBeGreaterThanOrEqual(4);
    expect(componentDetailSentences().length).toBeGreaterThanOrEqual(6);
    expect(queueDepthNetworkHint()).toContain('network-level block');
    expect(blobCheckpointFailures().map((f) => f.reasonKey).sort()).toEqual([
      'blobCheckpoint.authenticationFailed',
      'blobCheckpoint.keyAuthDisabled',
      'blobCheckpoint.permissionMismatch',
      'blobCheckpoint.storageFirewall',
      'blobCheckpoint.storageRejected',
      'blobCheckpoint.transport',
    ]);
  });

  it('translates every roll-up and section reason into Spanish', () => {
    const untranslated: string[] = [];
    for (const sentence of [...rollupSentences(), ...sectionSentences()]) {
      const states = sentence.text.includes('{input.WebhookState}') ? webhookStates() : ['-'];
      for (const state of states) {
        const sample = sampleOf(sentence, (hole) => {
          if (hole === 'input.WebhookState') return state;
          if (hole === 'c.Component') return 'BlobCheckpoint';
          return '7';
        });
        const translated = translateHealthReasonText(sample, es);
        if (leaksEnglish(sentence, translated)) untranslated.push(`${sample}  ->  ${translated}`);
      }
    }

    expect(
      untranslated,
      'These Health reasons reach a Spanish reader in English. Add a health.reason.* entry whose English\n' +
        'is the C# sentence, and recognise it in translateHealthReasonText:\n  ' +
        untranslated.join('\n  '),
    ).toEqual([]);
  });

  it('translates every component detail HealthService writes into Spanish', () => {
    const hint: CSharpSentence = { interpolated: false, text: queueDepthNetworkHint(), concatenated: false };
    const untranslated = componentDetailSentences()
      .flatMap((sentence) => {
        const sample = sampleOf(sentence);
        const checks = [{ sentence, sample }];
        // The failure detail also has a network-block variant: the same prefix with a hint appended.
        if (sentence.concatenated && sentence.text.includes('queue depth')) checks.push({ sentence: hint, sample: `${sample}${hint.text}` });
        return checks;
      })
      .map(({ sentence, sample }) => ({ sentence, sample, translated: translateHealthComponentDetailText(sample, es) }))
      .filter(({ sentence, translated }) => leaksEnglish(sentence, translated))
      .map(({ sample, translated }) => `${sample}  ->  ${translated}`);

    expect(
      untranslated,
      'These component details reach a Spanish reader in English. Add a health.reason.* entry whose English\n' +
        'is the C# sentence, and recognise it in translateHealthComponentDetailText:\n  ' +
        untranslated.join('\n  '),
    ).toEqual([]);
  });

  it('translates every blob checkpoint detail the importer writes, onto the key the Components table uses', () => {
    const source = blobCheckpointSource();
    const wrapper = /var healthDetail = \$"([^"]+)";/.exec(source)?.[1] ?? '';
    expect(wrapper, 'Could not find TrackDegradedHealth\'s detail').toContain('{classification.OperatorMessage}');

    const statusAndCode = 'HTTP 403 SampleErrorCode';
    const failures = blobCheckpointFailures().map(({ reasonKey, message }) => ({
      reasonKey,
      detail: wrapper.replace('{classification.OperatorMessage}', message.replace('{statusAndCode}', statusAndCode)),
    }));
    const tracked = [...source.matchAll(/TrackHealthCheck\(\s*HealthComponent\.BlobCheckpoint,\s*HealthStatus\.\w+,\s*"((?:[^"\\]|\\.)*)",\s*reasonKey:\s*"([^"]+)"\s*\)/g)]
      .map((m) => ({ reasonKey: m[2], detail: m[1] }));
    expect(tracked.map((t) => t.reasonKey).sort()).toEqual(['blobCheckpoint.healthy', 'blobCheckpoint.notConfigured']);

    for (const { reasonKey, detail } of [...failures, ...tracked]) {
      const expected = es(BLOB_CHECKPOINT_REASON_KEYS[reasonKey], { status: '403', errorCode: 'SampleErrorCode' });
      expect(translateHealthComponentDetailText(detail, es), reasonKey).toBe(expected);
      expect(translateHealthReasonText(`BlobCheckpoint is degraded: ${detail}`, es), `${reasonKey} in a roll-up`)
        .toBe(es('health.reason.componentDegraded', { component: es('health.component.BlobCheckpoint'), detail: expected }));
    }
  });
});

/**
 * Placeholder labels the server writes into DATA - the "(no department)" bucket, the "(other sites)"
 * roll-up - arrive as the value itself, with no key beside them, so the SPA recognises them by their
 * exact English (components/shared/serverPlaceholder.ts). This reads the C# and the SQL embedded in
 * it for every parenthesised placeholder it can write, and requires the SPA's list to match it
 * exactly: a new placeholder would otherwise show in English on every Spanish page it reaches.
 */
const SERVER_PLACEHOLDER_SOURCES = [
  ['Common', 'Entities', 'CopilotAdoption', 'CopilotAdoptionService.cs'],
  ['Common', 'Entities', 'CopilotAdoption', 'CopilotAdoptionEmailDomain.cs'],
  ['Common', 'Entities', 'CopilotAdoption', 'CopilotAdoptionSql.cs'],
  ['Common', 'Entities', 'Copilot', 'CopilotAccessedResourceTaxonomy.cs'],
  ['Common', 'Entities', 'SpoWebActivity', 'WebActivitySql.cs'],
  ['Common', 'Entities', 'TeamsExplorer', 'TeamsExplorerSql.cs'],
  ['Web', 'Controllers', 'ReportsAPIController.cs'],
  ['Web', 'Controllers', 'ReportsAPIController.OfficeApps.cs'],
  ['Web', 'Models', 'SystemStatus.cs'],
].map((parts) => join(process.cwd(), '..', '..', '..', ...parts));

/**
 * `"(no department)"`, `'(other sites)'`, `N'(untitled element)'` - any C# or SQL string literal whose
 * whole value is a parenthesised label. Deliberately not a list of the prefixes that exist today
 * ("no", "unknown", "other"...): round 5's first version was, and "(unnamed agent)" went straight
 * past it. Letters, spaces and hyphens only, so SQL such as `(u.account_enabled IS NULL OR ...)`
 * is not mistaken for a label.
 */
const SERVER_PLACEHOLDER_LITERAL = /(?:N?'|")(\([A-Za-z][A-Za-z \-]*\))(?:'|")/g;

/** Parenthesised literals in those files that never reach a page, and why. */
const NOT_DISPLAYED_PLACEHOLDERS: Record<string, string> = {
  '(all)': 'a cache-key fragment in ReportsAPIController, never returned',
  '(Any app)': 'the Office apps matrix ranking sentinel, filtered out before the rows are returned',
};

/** Every parenthesised label the scanned C# and SQL write in CODE - comments quote them too. */
function serverParenthesisedLiterals(): string[] {
  return sortedUnique(
    SERVER_PLACEHOLDER_SOURCES.flatMap((file) => {
      // Doc and line comments quote these labels too; only code can put one on a page.
      const code = readFileSync(file, 'utf8').replace(/^\s*\/\/.*$/gm, '');
      return [...code.matchAll(SERVER_PLACEHOLDER_LITERAL)].map((m) => m[1]);
    }),
  );
}

function serverPlaceholders(): string[] {
  return serverParenthesisedLiterals().filter((label) => !(label in NOT_DISPLAYED_PLACEHOLDERS));
}

describe('Server placeholder labels', () => {
  const es: TFunction = (key, values) => translateStatic('es', key, values);

  beforeAll(async () => {
    await loadCatalog('es');
  });

  it('finds the C# and SQL that write them', () => {
    for (const file of SERVER_PLACEHOLDER_SOURCES) {
      expect(() => readFileSync(file, 'utf8'), file).not.toThrow();
    }
    expect(serverPlaceholders()).toContain('(no department)');
    expect(serverPlaceholders().length).toBeGreaterThanOrEqual(20);

    // An exclusion the code no longer writes is a stale reason, not a harmless one - checked against
    // the same comment-free extraction, so a label that survives only in a comment does not keep it.
    const inCode = serverParenthesisedLiterals();
    expect(Object.keys(NOT_DISPLAYED_PLACEHOLDERS).filter((label) => !inCode.includes(label))).toEqual([]);
  });

  it('recognises every placeholder the server writes, and none it no longer writes', () => {
    const server = serverPlaceholders();
    const spa = sortedUnique(SERVER_PLACEHOLDER_KEYS.map((key) => EN_CATALOG[key]));

    expect(
      { missing: server.filter((label) => !spa.includes(label)), orphaned: spa.filter((label) => !server.includes(label)) },
      'The server placeholders and SERVER_PLACEHOLDER_KEYS must be an exact two-way match. Add a\n' +
        'common.serverPlaceholder.* entry whose English is the server\'s exact text, and list it in\n' +
        'components/shared/serverPlaceholder.ts.',
    ).toEqual({ missing: [], orphaned: [] });
  });

  it('translates each one, and leaves every other value exactly as the server sent it', () => {
    const untranslated = SERVER_PLACEHOLDER_KEYS.filter((key) => serverPlaceholderText(es, EN_CATALOG[key]) === EN_CATALOG[key]);
    expect(untranslated).toEqual([]);

    // Only a whole, exact value is a placeholder. A tenant's own names are never looked up.
    expect(serverPlaceholderText(es, 'Finance')).toBe('Finance');
    expect(serverPlaceholderText(es, 'Finance (no department)')).toBe('Finance (no department)');
    expect(serverPlaceholderText(es, '(No Department)')).toBe('(No Department)');
    expect(serverPlaceholderText(es, null)).toBeNull();
  });

  it('agrees with the accountability roll-up about the empty buckets they share', () => {
    for (const [emptyKey, catalogKey] of Object.entries(ACCOUNTABILITY_EMPTY_SEGMENT_KEYS)) {
      const placeholderKey = SERVER_PLACEHOLDER_KEYS.find((key) => EN_CATALOG[key] === EN_CATALOG[catalogKey]);
      expect(placeholderKey, `${emptyKey} has no server placeholder with the same English`).toBeTruthy();
      expect(es(placeholderKey!), emptyKey).toBe(es(catalogKey));
    }
  });
});

describe('Reports app-breadth bucket labels', () => {
  it('finds the Office apps query that produces app-count labels', () => {
    const source = readFileSync(REPORTS_CONTROLLERS[1], 'utf8');
    expect(source).toContain('internal static string AppBreadthQuery()');
    expect(source).toContain("CASE WHEN AppCount = 1 THEN N' app' ELSE N' apps' END AS Label");
  });

  it('has plural catalogue keys for the app-count labels', () => {
    expect(EN_CATALOG['reports.category.appBreadth.one']).toBe('{count} app');
    expect(EN_CATALOG['reports.category.appBreadth.other']).toBe('{count} apps');
  });
});

const TENURE_BASIS_CONST = /public\s+const\s+string\s+TenureBasis\w+\s*=\s*"([^"]+)";/g;

function tenureBasisKeys(): string[] {
  const source = readFileSync(COPILOT_ADOPTION_SCORING, 'utf8');
  return [...source.matchAll(TENURE_BASIS_CONST)].map((m) => m[1]);
}

describe('Copilot Adoption tenure-basis labels', () => {
  it('finds the scoring file that defines them', () => {
    expect(() => readFileSync(COPILOT_ADOPTION_SCORING, 'utf8')).not.toThrow();
    expect(tenureBasisKeys().length).toBeGreaterThanOrEqual(2);
  });

  it('has a SPA label map entry for every server tenure-basis code', () => {
    expect(Object.keys(TENURE_BASIS_LABEL_KEYS).sort()).toEqual(tenureBasisKeys().sort());
  });

  it('has catalog text for every mapped tenure-basis code', () => {
    const missing = Object.values(TENURE_BASIS_LABEL_KEYS).filter((catalogKey) => !(catalogKey in EN_CATALOG));
    expect(missing, 'Add missing tenure-basis display text to copilotAdoptionUsers in en/es.').toEqual([]);
  });
});

describe('Copilot Adoption Cowork row tier labels', () => {
  it('finds the scoring file that defines them', () => {
    expect(() => readFileSync(COPILOT_ADOPTION_SCORING, 'utf8')).not.toThrow();
    expect(coworkTierKeys().length).toBeGreaterThanOrEqual(6);
  });

  it('has a SPA label map entry for every server Cowork tier code', () => {
    expect(Object.keys(COWORK_TIER_LABEL_KEYS).sort()).toEqual(coworkTierKeys().sort());
  });
});

describe('Service Configuration webhook status details', () => {
  it('translates every fixed webhook detail SystemStatus writes, and nothing else is a sentence', async () => {
    const { WEBHOOK_STATUS_DETAIL_TEXT } = await import('../../pages/ServiceConfigurationPage');
    const source = readFileSync(join(process.cwd(), '..', '..', 'Models', 'SystemStatus.cs'), 'utf8');
    const opened = [...source.matchAll(/\bCallWebhookStatusDetail\s*\+?=(?!=)/g)].length;
    const assigned = [...source.matchAll(/\bCallWebhookStatusDetail\s*=\s*("(?:[^"\\]|\\.)*"|[\w.]+)\s*;/g)].map((m) => m[1]);
    expect(assigned.length, 'a CallWebhookStatusDetail assignment the gate cannot read').toBe(opened);

    // The non-literal assignments carry data, not sentences: the cached detail and an exception message.
    expect(assigned.filter((value) => !value.startsWith('"')).sort()).toEqual(['cached.Detail', 'ex.Message']);
    const sentences = assigned.filter((value) => value.startsWith('"')).map((value) => csharpStringLiteralValue(value.slice(1, -1)));
    expect(sentences).toEqual(["WebAppURL is not configured, so the webhook subscription URL can't be determined."]);
    expect(sortedUnique(Object.keys(WEBHOOK_STATUS_DETAIL_TEXT)), 'WEBHOOK_STATUS_DETAIL_TEXT must translate exactly these').toEqual(sortedUnique(sentences));
    expect(EN_CATALOG['admin.serviceConfiguration.webhook.detail.webAppUrlMissing']).toBe(sentences[0]);
  });
});

describe('Service Configuration update-check errors', () => {
  // UpdateChecker's interpolations, named as the SPA catalog's placeholders. An interpolation not listed
  // here survives as `{...C#...}` and fails the comparison below - as does a new, removed or reworded
  // sentence.
  const HOLES: Record<string, string> = {
    '{_timeout.TotalSeconds:0}': '{seconds}',
    '{InnerMostMessage(ex)}': '{error}',
    '{currentLabel}': '{label}',
    '{resetText}': '{resetAt}',
    '{(int)response.StatusCode}': '{status}',
    '{response.StatusCode}': '{statusName}',
  };

  function updateCheckerSource(): string {
    return readFileSync(join(process.cwd(), '..', '..', 'Models', 'UpdateCheck', 'UpdateChecker.cs'), 'utf8');
  }

  /** Every `result.Error = "..."`, `result.CheckError = "..."` and `return "..."` sentence, literals joined. */
  function serverSentences(): string[] {
    return [...updateCheckerSource().matchAll(/(?:result\.(?:Check)?Error\s*=|return)\s*((?:\$?"(?:[^"\\]|\\.)*"\s*\+?\s*)+);/g)]
      .map((m) => [...m[1].matchAll(/\$?"((?:[^"\\]|\\.)*)"/g)].map((part) => csharpStringLiteralValue(part[1])).join(''))
      .map((sentence) => sentence.replace(/\{[^}]+\}/g, (hole) => HOLES[hole] ?? hole));
  }

  it('recognises every fixed sentence UpdateChecker can report, with exact English', async () => {
    const { UPDATE_CHECK_ERROR_KEYS } = await import('../../pages/ServiceConfigurationPage');
    const server = serverSentences();
    expect(server.length, 'UpdateChecker error sentences not found').toBe(9);

    const templated = UPDATE_CHECK_ERROR_KEYS
      .filter((key) => key !== 'admin.serviceConfiguration.updates.error.rateLimitedSoon')
      .map((key) => EN_CATALOG[key]);
    expect(sortedUnique(templated), 'UPDATE_CHECK_ERROR_KEYS must match UpdateChecker sentence for sentence').toEqual(sortedUnique(server));
  });

  it('keeps the no-reset-time rate-limit sentence equal to what the server writes', () => {
    expect(updateCheckerSource()).toContain('var resetText = "shortly";');
    expect(EN_CATALOG['admin.serviceConfiguration.updates.error.rateLimitedSoon'])
      .toBe(EN_CATALOG['admin.serviceConfiguration.updates.error.rateLimited'].replace('{resetAt}', 'shortly'));
  });
});

describe('Teams authorisation server errors', () => {
  it('keeps the Redis prerequisite error aligned with the SPA catalog entry', () => {
    const source = readFileSync(join(process.cwd(), '..', '..', 'Controllers', 'TeamsAuthAPIController.cs'), 'utf8');
    // Every ApiErrorModel the controller builds, literals joined: teamAuthErrorText matches the sentence
    // exactly, so text appended on the server, or a second sentence, would reach a Spanish reader in English.
    const opened = [...source.matchAll(/new ApiErrorModel\(/g)].length;
    const sentences = [...source.matchAll(/new ApiErrorModel\(\s*((?:"(?:[^"\\]|\\.)*"\s*\+?\s*)+)\)/g)]
      .map((m) => [...m[1].matchAll(/"((?:[^"\\]|\\.)*)"/g)].map((part) => csharpStringLiteralValue(part[1])).join(''));
    expect(sentences.length, 'an ApiErrorModel whose message is not literal text').toBe(opened);
    expect(sentences).toEqual([EN_CATALOG['admin.teams.teamList.redisNotConfigured']]);
    // A reply carrying text some other way (an anonymous { message }) would skip the check above.
    expect([...source.matchAll(/\bContent\s*\(/g)].length, 'a Teams authorisation Content(...) reply that is not an ApiErrorModel')
      .toBe([...source.matchAll(/\bContent\s*\(\s*HttpStatusCode\.\w+,\s*new ApiErrorModel\(/g)].length);
  });
});

describe('User lookup server errors', () => {
  const service = () => readFileSync(join(process.cwd(), '..', '..', 'Models', 'UserDataLookup', 'UserDataLookupService.cs'), 'utf8');
  const controller = () => readFileSync(join(process.cwd(), '..', '..', 'Controllers', 'UserDataLookupAPIController.cs'), 'utf8');

  it('catalogues every sentence UserDataLookupService writes, with its interpolations as placeholders', () => {
    const source = service();
    const opened = [...source.matchAll(/\.(?:BadRequest|UserNotFound)\(/g)].length;
    const sentences = [...source.matchAll(/\.(?:BadRequest|UserNotFound)\(\s*\$?"((?:[^"\\]|\\.)*)"\s*\)/g)].map((m) => csharpStringLiteralValue(m[1]));
    expect(sentences.length, 'a BadRequest/UserNotFound whose message is not one literal').toBe(opened);
    expect(sortedUnique(sentences)).toEqual(sortedUnique([
      EN_CATALOG['errors.userLookup.missingUpn'],
      EN_CATALOG['errors.userLookup.unknownCategory'],
      EN_CATALOG['errors.userLookup.categoryNoDrilldown'],
      EN_CATALOG['errors.userLookup.notFound'],
    ]));
  });

  it('keeps the controller deriving each code from those same sentences, and sending the UPN fact', () => {
    // BadRequestError reads the service's English to pick a code (a known soft spot): pin its prefixes to
    // the catalog, which the spec above pins to the service, so a reword fails here, not in production.
    const source = controller().replace(/\s+/g, ' ');
    const [unknownPrefix, unknownSuffix] = EN_CATALOG['errors.userLookup.unknownCategory'].split('{category}');
    const [noDrilldownPrefix, noDrilldownSuffix] = EN_CATALOG['errors.userLookup.categoryNoDrilldown'].split('{category}');
    expect(source).toContain(`if (message == "${EN_CATALOG['errors.userLookup.missingUpn']}")`);
    expect(source).toContain(`const string unknownPrefix = "${unknownPrefix}";`);
    expect(source).toContain(`message.EndsWith("${unknownSuffix}")`);
    expect(source).toContain(`const string noDrilldownPrefix = "${noDrilldownPrefix}";`);
    expect(source).toContain(`const string noDrilldownSuffix = "${noDrilldownSuffix}";`);
    expect(source).toContain('new ApiErrorModel(result.ErrorMessage, "userNotFound") { Upn = UserDataLookupRules.Normalise(upn), }');
  });

  it('writes no sentence of its own in the controller: every error carries the service message', () => {
    const source = controller();
    // The controller only relays UserDataLookupService's sentences (pinned above). A literal here, or a
    // reply that is not an ApiErrorModel, would reach Spanish readers in English.
    const firstArgs = [...source.matchAll(/new ApiErrorModel\(\s*([^,)]+)/g)].map((m) => m[1].trim());
    expect(sortedUnique(firstArgs), 'ApiErrorModel message arguments').toEqual(['message', 'result.ErrorMessage']);
    const contents = [...source.matchAll(/\bContent\s*\(\s*HttpStatusCode\.(\w+),\s*([A-Za-z]+)/g)].map((m) => `${m[1]}:${m[2]}`);
    expect(contents.length, 'a Content(...) reply the gate cannot read').toBe([...source.matchAll(/\bContent\s*\(/g)].length);
    expect(contents.sort()).toEqual(['BadRequest:BadRequestError', 'NotFound:new']);
  });

  it('recognises every fixed row title and detail prefix SqlUserDataLookupQuery writes', async () => {
    const { detailText, detailTitle } = await import('../../components/userlookup/CategoryRow');
    const query = readFileSync(join(process.cwd(), '..', '..', 'Models', 'UserDataLookup', 'SqlUserDataLookupQuery.cs'), 'utf8');
    const titles = [...query.matchAll(/\bTitle\s*=\s*([^,\n]+)/g)]
      .flatMap((m) => [...m[1].matchAll(/"((?:[^"\\]|\\.)*)"/g)].map((s) => csharpStringLiteralValue(s[1])));
    const prefixes = [...query.matchAll(/\bDetail\s*=\s*([^\n]+)/g)]
      .flatMap((m) => [...m[1].matchAll(/"((?:[^"\\]|\\.)*)"\s*\+/g)].map((s) => csharpStringLiteralValue(s[1])));

    // Each title with the category it appears under; a new or reworded literal fails the equality.
    const titleCategories: Record<string, string> = {
      'Call / meeting': 'calls-organised',
      'Call session attended': 'call-sessions',
      'Activity report day': 'usage-teams',
      '(audit event)': 'audit-events',
    };
    expect(sortedUnique(titles)).toEqual(sortedUnique(Object.keys(titleCategories)));
    expect(sortedUnique(prefixes)).toEqual(['Ended ', 'Last activity ']);

    await loadCatalog('es');
    const es: TFunction = (key, values) => translateStatic('es', key, values);
    for (const [title, category] of Object.entries(titleCategories)) {
      expect(detailTitle(es, category, title), `detailTitle does not recognise "${title}"`).not.toBe(title);
    }
    expect(detailText(es, 'calls-organised', { detail: 'Ended 2026-09-01 10:00:00Z' } as never)).not.toMatch(/^Ended /);
    expect(detailText(es, 'usage-teams', { detail: 'Last activity 9/1/2026' } as never)).not.toMatch(/^Last activity /);
  });
});

// --- Licence Activity coverage measures and messages -------------------------------------------------
//
// The licence activity SQL writes each workload's coverage `measure` and `message` as English sentences.
// LicenceActivityDisplayKeys (C#) turns each sentence into a stable key, and sources.ts maps that key to a
// catalog entry. Three kinds of drift each put English back on the Spanish page without failing anything
// else: a sentence the C# switch does not know, a switch case whose sentence no longer exists (reworded),
// and a key that one side has and the other lacks.

const LICENCE_ACTIVITY_DIR = join(process.cwd(), '..', '..', '..', 'Common', 'Entities', 'LicenceActivity');

function licenceDisplayKeySwitch(name: 'MeasureKeyFor' | 'MessageKeyFor'): Map<string, string> {
  const source = readFileSync(join(LICENCE_ACTIVITY_DIR, 'LicenceActivityModels.cs'), 'utf8');
  const start = source.indexOf(`static string ${name}(`);
  expect(start, `LicenceActivityDisplayKeys.${name} not found`).toBeGreaterThanOrEqual(0);
  const body = source.slice(start, source.indexOf('default:', start));
  return new Map(
    [...body.matchAll(/case "((?:[^"\\]|\\.)*)": return "([^"]+)";/g)].map((m) => [csharpStringLiteralValue(m[1]), m[2]]),
  );
}

/** Every coverage measure and message sentence the server can write, from the three places it writes them. */
function licenceCoverageSentences(): string[] {
  const sql = readFileSync(join(LICENCE_ACTIVITY_DIR, 'LicenceActivitySql.cs'), 'utf8');
  // SQL literals, apart from diagnostics (PRINT) and dynamic SQL (sp_executesql). Two or more words, or
  // anything ending like a sentence, is text someone reads; codes ('available') and bucket names
  // (N'Unknown') are single bare words.
  const sqlSentences = [...sql.matchAll(/(PRINT\s+|sp_executesql\s+)?N'((?:[^']|'')*)'/g)]
    .filter((m) => !m[1])
    .map((m) => m[2].replace(/''/g, "'"))
    .filter((literal) => literal.trim().split(/\s+/).length >= 2 || /[.!?]$/.test(literal.trim()));
  // The Microsoft 365 measures are passed from C# into the SQL template; the store also builds coverage in C#.
  const m365Measures = [...sql.matchAll(/AppendM365Overview\([^;]*?"((?:[^"\\]|\\.)*)",\s*sources\.UsageReports\)/g)]
    .map((m) => csharpStringLiteralValue(m[1]));
  // C# text interpolated into the SQL as N'{n}': the two M365 overview measures above, and the Copilot
  // fallback row's initialMessage. Every such format item is counted, so a new C#-written sentence cannot
  // hide behind one the way initialMessage once did.
  const formatItems = sql.split('\n').filter((line) => !line.trim().startsWith('//')).join('\n').match(/N'\{\d\}'/g) ?? [];
  expect(formatItems.length, "a new N'{n}' SQL literal is written from C#: extract its text in licenceCoverageSentences").toBe(3);
  const initialMessage = /var initialMessage\s*=([^;]*);/.exec(sql)?.[1];
  expect(initialMessage, 'LicenceActivitySql initialMessage not found').toBeTruthy();
  // One sentence per branch of the ternary, joining any literals a long line was wrapped into.
  const copilotFallback: string[] = [];
  let branch: string[] | null = null;
  for (const token of initialMessage!.matchAll(/"((?:[^"\\]|\\.)*)"|([?:])/g)) {
    if (token[2]) {
      if (branch) copilotFallback.push(branch.join(''));
      branch = [];
    } else {
      branch?.push(csharpStringLiteralValue(token[1]));
    }
  }
  if (branch) copilotFallback.push(branch.join(''));
  expect(copilotFallback.length, 'initialMessage sentences').toBe(2);
  // Every coverage builder: a literal Measure/Message is a sentence; anything else may only read or copy
  // one, so an interpolated, computed or defaulted (`?? "..."`) sentence fails here rather than going unkeyed.
  const builders = ['SqlLicenceActivityStore.cs', 'SqlLicenceActivityReadModelLoader.cs', 'LicenceActivityReadModel.cs']
    .map((file) => readFileSync(join(LICENCE_ACTIVITY_DIR, file), 'utf8'));
  const assignments = builders.flatMap((source) => [...source.matchAll(
    /\b(?:Measure|Message)\s*=(?![=>])\s*("(?:[^"\\]|\\.)*"|\w+\([^)]*\)(?=\s*[,}\r\n])|[^,\n]+)/g,
  )].map((m) => m[1].trim()));
  expect(sortedUnique(assignments.filter((value) => !value.startsWith('"'))), 'a non-literal coverage Measure/Message in a coverage builder')
    .toEqual(sortedUnique([
      'ReadNullableString(reader, "Message")', 'ReadString(reader, "Measure")', 'string.Empty',
      'coverage.Measure', 'source.Measure', 'source.Message', '_coverage[workload].Measure',
    ]));
  const storeText = assignments.filter((value) => value.startsWith('"')).map((value) => csharpStringLiteralValue(value.slice(1, -1)));
  return sortedUnique([...sqlSentences, ...m365Measures, ...copilotFallback, ...storeText]);
}

describe('Licence Activity coverage measures and messages', () => {
  it('finds the sentences it is guarding', () => {
    // A parser that silently matched nothing would make every test below vacuous.
    expect(licenceCoverageSentences().length).toBeGreaterThanOrEqual(25);
    expect(licenceDisplayKeySwitch('MeasureKeyFor').size).toBeGreaterThanOrEqual(8);
    expect(licenceDisplayKeySwitch('MessageKeyFor').size).toBeGreaterThanOrEqual(15);
  });

  it('gives every sentence the server writes a stable key', () => {
    const known = new Set([...licenceDisplayKeySwitch('MeasureKeyFor').keys(), ...licenceDisplayKeySwitch('MessageKeyFor').keys()]);
    const unkeyed = licenceCoverageSentences().filter((sentence) => !known.has(sentence));

    expect(unkeyed, 'add each sentence to LicenceActivityDisplayKeys, then map its key in sources.ts').toEqual([]);
  });

  it('has no key for a sentence the server no longer writes', () => {
    const written = new Set(licenceCoverageSentences());
    const stale = [...licenceDisplayKeySwitch('MeasureKeyFor').keys(), ...licenceDisplayKeySwitch('MessageKeyFor').keys()]
      .filter((sentence) => !written.has(sentence));

    expect(stale, 'a reworded sentence: update the switch, the English catalog text, and re-read its Spanish').toEqual([]);
  });

  it('maps exactly the keys the server sends, to catalog text that reads as the server does', async () => {
    const { MEASURE_LABELS, MESSAGE_LABELS } = await import('../../components/licenceActivity/sources');

    for (const [name, spaMap] of [['MeasureKeyFor', MEASURE_LABELS], ['MessageKeyFor', MESSAGE_LABELS]] as const) {
      const server = licenceDisplayKeySwitch(name);
      expect(Object.keys(spaMap).sort(), `${name}: SPA keys vs server keys`).toEqual(sortedUnique([...server.values()]));

      // The English page must read exactly as it did when the server's sentence was shown directly.
      for (const [sentence, key] of server) {
        expect(EN_CATALOG[spaMap[key]], `${name}: English catalog text for '${key}'`).toBe(sentence);
      }
    }
  });
});


// --- Copilot Adoption structured warnings -----------------------------------------------------------

const COPILOT_ADOPTION_SERVICE = join(process.cwd(), '..', '..', '..', 'Common', 'Entities', 'CopilotAdoption', 'CopilotAdoptionService.cs');
const COPILOT_ADOPTION_SUMMARY_MODELS = join(process.cwd(), '..', '..', '..', 'Common', 'Entities', 'CopilotAdoption', 'CopilotAdoptionSummaryModels.cs');
const COPILOT_ADOPTION_SERVER_TEXT = join(process.cwd(), 'components', 'copilotAdoption', 'serverText.ts');

function copilotWarningConstants(): Record<string, string> {
  const source = readFileSync(COPILOT_ADOPTION_SUMMARY_MODELS, 'utf8');
  const body = /public static class CopilotAdoptionWarningKeys\s*\{([\s\S]*?)\n\s*\}/.exec(source)?.[1] ?? '';
  return Object.fromEntries([...body.matchAll(/public const string (\w+)\s*=\s*"([^"]+)";/g)].map((m) => [m[1], m[2]]));
}

function copilotWarningTemplates(): Record<string, string> {
  const source = readFileSync(COPILOT_ADOPTION_SUMMARY_MODELS, 'utf8');
  const constants = copilotWarningConstants();
  const body = /private static readonly Dictionary<string, string> Templates[\s\S]*?\{([\s\S]*?)\n\s*\};/.exec(source)?.[1] ?? '';
  return Object.fromEntries([...body.matchAll(/\{\s*CopilotAdoptionWarningKeys\.(\w+),\s*"((?:[^"\\]|\\.)*)"\s*\}/g)]
    .map((m) => [constants[m[1]], csharpStringLiteralValue(m[2])]));
}

function placeholders(text: string): string[] {
  return sortedUnique([...text.matchAll(/\{([^}]+)\}/g)].map((m) => m[1]));
}

function copilotQueryDescriptions(): Record<string, string> {
  const source = readFileSync(COPILOT_ADOPTION_SERVICE, 'utf8');
  return Object.fromEntries([...source.matchAll(
    /CopilotAdoptionQueries\.(\w+),\s*(?:summary\.Warnings,\s*summary\.WarningDetails,\s*)?(?:output,\s*)?\r?\n\s*"([^"]+)"/g,
  )].map((m) => [m[1], m[2]]));
}

describe('Copilot Adoption server warning text', () => {
  it('has a SPA mapping and exact English catalog template for every server warning key', () => {
    const server = copilotWarningTemplates();
    const mapped = Object.values(COPILOT_ADOPTION_WARNING_KEYS);
    expect({
      missing: Object.keys(server).filter((key) => !mapped.includes(key as never)),
      orphaned: mapped.filter((key) => !(key in server)),
    }, 'Keep COPILOT_ADOPTION_WARNING_KEYS in serverText.ts in exact sync with CopilotAdoptionWarningKeys/WarningTemplates in C#.').toEqual({ missing: [], orphaned: [] });

    for (const [key, english] of Object.entries(server)) {
      if (key === COPILOT_ADOPTION_WARNING_KEYS.UsageReportSourcedUsers) {
        const one = 'copilotAdoption.server.warning.usageReportSourcedUsers.one';
        const other = 'copilotAdoption.server.warning.usageReportSourcedUsers.other';
        expect(EN_CATALOG[one], `${one} must reproduce the singular server English.`)
          .toBe(english.replace('{userPlural}', ''));
        expect(EN_CATALOG[other], `${other} must reproduce the plural server English.`)
          .toBe(english.replace('{userPlural}', 's'));
        expect(placeholders(EN_CATALOG[one]), `${one} placeholders`).toEqual(['count', 'percentage']);
        expect(placeholders(EN_CATALOG[other]), `${other} placeholders`).toEqual(['count', 'percentage']);
        continue;
      }
      const catalogKey = `copilotAdoption.server.warning.${key}`;
      expect(EN_CATALOG[catalogKey], `${catalogKey} must reproduce the server English exactly.`).toBe(english);
      expect(placeholders(EN_CATALOG[catalogKey]), `${catalogKey} placeholders`).toEqual(placeholders(english));
    }
  });

  it('catalogues every could-not-load query description by stable query name', () => {
    const descriptions = copilotQueryDescriptions();
    expect(Object.keys(descriptions).length).toBeGreaterThanOrEqual(20);
    for (const [query, description] of Object.entries(descriptions)) {
      expect(EN_CATALOG[`copilotAdoption.server.query.${query}`], `copilotAdoption.server.query.${query}`)
        .toBe(description);
    }
  });

  it('adds every warning through the structured helpers, so warningDetails stays aligned', () => {
    const service = readFileSync(COPILOT_ADOPTION_SERVICE, 'utf8');
    const directAdds = [...service.matchAll(/\bWarnings\s*\.\s*(?:Add|AddRange|Insert|InsertRange)\s*\(([^;]*);/g)]
      .map((m) => m[0].replace(/\s+/g, ''));

    // The one direct add is the step merge, which copies a step's already-paired warnings and details.
    expect(directAdds, 'Use CopilotAdoptionWarnings.Add(...) or StepOutput.AddWarning(...) so warningDetails stays aligned.')
      .toEqual(['Warnings.Add(warning);']);
    const flat = service.replace(/\s+/g, '');
    expect(flat).toContain('analysis.Summary.Warnings.Add(warning);');
    expect(flat).toContain('analysis.Summary.WarningDetails.Add(detail.Clone());');

    // A floor, so a moved or renamed service fails here instead of passing on an empty match.
    const structured = [...service.matchAll(/\bCopilotAdoptionWarnings\.Add\s*\(|\bAddWarning\s*\(/g)].length;
    expect(structured, 'structured warning calls not found').toBeGreaterThanOrEqual(20);

    for (const file of ['CopilotAdoptionScope.cs', 'CopilotAdoptionCoworkModels.cs']) {
      const source = readFileSync(join(process.cwd(), '..', '..', '..', 'Common', 'Entities', 'CopilotAdoption', file), 'utf8');
      expect([...source.matchAll(/\bWarnings\s*\.\s*(?:Add|AddRange|Insert|InsertRange)\s*\(/g)], `${file} adds unstructured warnings`).toEqual([]);
    }
  });

  it('keeps the reclaim caveat key catalogued beside the server fallback', () => {
    const source = readFileSync(COPILOT_ADOPTION_SERVICE, 'utf8');
    const fallback = /summary\.ReclaimCaveat\s*=\s*"((?:[^"\\]|\\.)*)";/.exec(source)?.[1] ?? '';
    expect(source).toContain('summary.ReclaimCaveatKey = CopilotAdoptionWarningKeys.ReclaimCaveat;');
    expect(EN_CATALOG['copilotAdoption.server.reclaimCaveat']).toBe(csharpStringLiteralValue(fallback));
  });

  it('does not filter Copilot Adoption warning panels by English substrings', () => {
    const sources = [
      join(process.cwd(), 'src', 'components', 'copilotAdoption', 'CoworkPanel.tsx'),
      join(process.cwd(), 'src', 'components', 'copilotAdoption', 'OpportunitiesPanel.tsx'),
    ].map((file) => readFileSync(file, 'utf8')).join('\n');

    expect(sources).not.toMatch(/includes\(['"](cowork|licence opportunit|usage report)/i);
    expect(sources).toContain('isCoworkWarning');
    expect(sources).toContain('isLicenceOpportunityWarning');
  });

  it('shows every Cowork query failure on the Cowork tab', async () => {
    const { isCoworkWarning } = await import('../../components/copilotAdoption/serverText');
    const service = readFileSync(COPILOT_ADOPTION_SERVICE, 'utf8');
    const coworkQueries = sortedUnique([...service.matchAll(/CopilotAdoptionQueries\.(Cowork\w+)/g)].map((m) => m[1]));

    expect(coworkQueries.length, 'Cowork queries not found in the service').toBeGreaterThanOrEqual(7);
    expect(
      coworkQueries.filter((query) => !isCoworkWarning({ key: 'couldNotLoad', values: { query, description: '', message: '' } })),
      'Add each Cowork query to COWORK_WARNING_QUERIES in serverText.ts, or its failure disappears from the Cowork tab.',
    ).toEqual([]);
  });

  it('names every figures-incomplete dataset from the catalog, with the service English verbatim', async () => {
    const { INCOMPLETE_DATASET_KEYS } = await import('../../components/copilotAdoption/serverText');
    const service = readFileSync(COPILOT_ADOPTION_SERVICE, 'utf8');
    // A literal is parsed whole (a name may itself contain parentheses), and every call must parse: an
    // argument the pattern cannot read - an interpolation, a concatenation - fails the count below.
    const opened = [...service.matchAll(/\bMark(?:FiguresIncomplete|Incomplete)\s*\(/g)].length;
    const calls = [...service.matchAll(/\bMark(?:FiguresIncomplete|Incomplete)\s*\(\s*("(?:[^"\\]|\\.)*"|[^)"]*?)\s*\)/g)].map((m) => m[1].trim());
    expect(calls.length, 'a MarkFiguresIncomplete/MarkIncomplete argument the gate cannot read').toBe(opened);
    const literals = calls.filter((arg) => /^"(?:[^"\\]|\\.)*"$/.test(arg)).map((arg) => csharpStringLiteralValue(arg.slice(1, -1)));

    // The only non-literal uses are StepOutput.MarkIncomplete's own parameter and the step merge, which
    // forwards reasons recorded by literal calls above. Anything else could name a dataset this list misses.
    expect(sortedUnique(calls.filter((arg) => !arg.startsWith('"'))), 'non-literal dataset name').toEqual(['reason', 'string dataset']);
    expect(literals.length, 'figures-incomplete datasets not found in the service').toBeGreaterThanOrEqual(17);
    // Only the service names datasets: a call in any other CopilotAdoption file would escape the list below.
    const adoptionDir = join(process.cwd(), '..', '..', '..', 'Common', 'Entities', 'CopilotAdoption');
    for (const file of readdirSync(adoptionDir).filter((name) => name.endsWith('.cs') && name !== 'CopilotAdoptionService.cs')) {
      const other = readFileSync(join(adoptionDir, file), 'utf8');
      const foreign = [...other.matchAll(/\bMark(?:FiguresIncomplete|Incomplete)\s*\(([^)]*)\)/g)].map((m) => m[1].trim())
        .filter((arg) => arg !== 'string dataset');
      expect(foreign, `${file} names a figures-incomplete dataset`).toEqual([]);
    }
    const controllerSource = readFileSync(join(process.cwd(), '..', '..', 'Controllers', 'CopilotAdoptionAPIController.cs'), 'utf8');
    expect([...controllerSource.matchAll(/\bMark(?:FiguresIncomplete|Incomplete)\s*\(/g)], 'the controller names a dataset').toEqual([]);
    expect(sortedUnique(INCOMPLETE_DATASET_KEYS.map((key) => EN_CATALOG[key])), 'INCOMPLETE_DATASET_KEYS must name exactly the service datasets, verbatim')
      .toEqual(sortedUnique(literals));
    expect(INCOMPLETE_DATASET_KEYS.length, 'two dataset keys share one English name').toBe(sortedUnique(literals).length);
  });
});

describe('Reports partial-data warning facts', () => {
  it('catalogues every structured usage-series warning reason the server can send', () => {
    const source = readFileSync(join(process.cwd(), '..', '..', 'Controllers', 'ReportsAPIController.cs'), 'utf8');
    const reasons = [...source.matchAll(/Reason\s*=\s*"([^"]+)"/g)].map((m) => m[1]).sort();

    expect(reasons).toEqual(['loadFailed', 'noSettledData', 'noSettledDataForWeek', 'notAttempted']);

    const missing = reasons
      .map((reason) => `reports.chart.warning.series.${reason}`)
      .filter((key) => !(key in EN_CATALOG));

    expect(
      missing,
      'ReportsAPIController added a structured partial-data warning reason without a matching\n' +
        'catalog entry, so the SPA would fall back to server-authored English. Add the key to\n' +
        'src/i18n/catalog/{en,es}/reports.ts.',
    ).toEqual([]);
  });

  it('catalogues every structured usage-series chart error the server can send', () => {
    const source = readFileSync(join(process.cwd(), '..', '..', 'Controllers', 'ReportsAPIController.cs'), 'utf8');
    const errorStatements = [...source.matchAll(/ErrorKey\s*=[^;]+;/g)].map((m) => m[0]).join('\n');
    const errorKeys = [...errorStatements.matchAll(/"([^"]+)"/g)].map((m) => m[1]).sort();

    expect(errorKeys).toEqual(['noCompletedUsageWeeks', 'noCompletedUsageWeeksWithData', 'noWorkloadSeriesLoaded']);

    const missing = errorKeys
      .map((errorKey) => `reports.chart.error.${errorKey}`)
      .filter((key) => !(key in EN_CATALOG));

    expect(
      missing,
      'ReportsAPIController added a structured chart error key without a matching catalog entry.',
    ).toEqual([]);
  });
});

describe('API error-code drift checks', () => {
  function quotedArguments(call: string): string[] {
    return [...call.matchAll(/"((?:[^"\\]|\\.)*)"/g)].map((m) => csharpStringLiteralValue(m[1]));
  }

  function licenceActivityServerErrors(): Map<string, string> {
    const controller = readFileSync(join(process.cwd(), '..', '..', 'Controllers', 'LicenceActivityAPIController.cs'), 'utf8');
    const pairs = new Map<string, string>();

    for (const match of controller.matchAll(/Error\(\s*"((?:[^"\\]|\\.)*)"\s*,\s*((?:.|\n)*?)\)/g)) {
      const code = csharpStringLiteralValue(match[1]);
      const message = quotedArguments(match[2]).join('');
      if (message) pairs.set(code, message);
    }

    const switchStart = controller.indexOf('static string ValidationErrorCode');
    expect(switchStart, 'ValidationErrorCode switch not found').toBeGreaterThanOrEqual(0);
    const switchBody = controller.slice(switchStart, controller.indexOf('default:', switchStart));
    for (const match of switchBody.matchAll(/case\s+"((?:[^"\\]|\\.)*)"\s*:\s*return\s+"((?:[^"\\]|\\.)*)"/g)) {
      pairs.set(csharpStringLiteralValue(match[2]), csharpStringLiteralValue(match[1]));
    }

    // The failed-run 503 sends the exception's own sentence, with the run id beside it as `reference`.
    const failed = /new \{ code = "(\w+)", message = ex\.Message, reference = ex\.RunId \}/.exec(controller);
    expect(failed, 'LicenceActivityFailedException reply not found').toBeTruthy();
    const cache = readFileSync(join(process.cwd(), '..', '..', 'Models', 'LicenceActivity', 'LicenceActivitySnapshotCache.cs'), 'utf8');
    const prefix = /LicenceActivityFailedException\(string runId\)\s*:\s*base\("((?:[^"\\]|\\.)*)" \+ runId\)/.exec(cache)?.[1];
    expect(prefix, 'LicenceActivityFailedException message not found').toBeTruthy();
    pairs.set(failed![1], `${csharpStringLiteralValue(prefix!)}{reference}`);

    // Any other anonymous-object code would be invisible to the extraction above: fail rather than miss it.
    const anonymousCodes = [...controller.matchAll(/\bcode = "(\w+)"/g)].map((m) => m[1]);
    expect(anonymousCodes.filter((code) => !pairs.has(code)), 'Unextracted LicenceActivityAPIController error code').toEqual([]);

    return pairs;
  }

  it('keeps Licence Activity server error codes aligned with SPA mappings and English text', async () => {
    const { ERROR_CODE_KEYS } = await import('../../api/licenceActivityApi');
    const server = licenceActivityServerErrors();
    const serverCodes = [...server.keys()].sort();
    const spaCodes = Object.keys(ERROR_CODE_KEYS).sort();

    expect(spaCodes, 'SPA maps exactly the LicenceActivityAPIController codes it can send').toEqual(serverCodes);

    for (const [code, message] of server) {
      const catalogKey = ERROR_CODE_KEYS[code];
      expect(catalogKey, `Missing SPA map entry for Licence Activity error code '${code}'`).toBeTruthy();
      expect(EN_CATALOG[catalogKey], `English catalog for Licence Activity error code '${code}' must match the server`).toBe(message);
    }
  });

  function agentCostServerErrors(): Map<string, string> {
    const source = readFileSync(join(process.cwd(), '..', '..', 'Controllers', 'AgentCostsAPIController.cs'), 'utf8');
    const pairs = new Map<string, string>();
    for (const match of source.matchAll(/\bcode\s*=\s*"(\w+)",\s*message\s*=\s*((?:\$?"(?:[^"\\]|\\.)*"\s*\+?\s*)+),/g)) {
      pairs.set(match[1], quotedArguments(match[2]).join(''));
    }

    // Every coded response, however it is written: one whose message is not a plain literal fails here.
    const codes = [...source.matchAll(/\bcode\s*=\s*"(\w+)"/g)].map((m) => m[1]);
    expect(codes.length, 'Agent Costs coded responses not found').toBeGreaterThan(0);
    expect(codes.filter((code) => !pairs.has(code)), 'Agent Costs error code whose message could not be extracted').toEqual([]);

    // Replies WITHOUT a code are shown to the reader as the server wrote them. Only these three are allowed:
    // bad-request replies to malformed requests the portal never makes. A new one would be English on a
    // Spanish page, so it must get a code and a catalog entry instead.
    const replies = [...source.matchAll(/\bContent\s*\(\s*HttpStatusCode\.\w+,\s*new\b[^;]*;/g)].map((m) => m[0]);
    // Every Content(...) reply must be one this gate can read: a body built elsewhere and passed in as a
    // variable would otherwise be invisible to both checks below.
    expect(replies.length, 'an Agent Costs Content(...) reply whose body is not written inline').toBe([...source.matchAll(/\bContent\s*\(/g)].length);
    const uncoded = replies
      .filter((reply) => !/\bcode\s*=/.test(reply))
      .map((reply) => /\bmessage\s*=\s*(\$?"(?:[^"\\]|\\.)*"|[\w.]+)/.exec(reply)?.[1] ?? reply);
    expect(replies.length, 'Agent Costs replies not found').toBeGreaterThanOrEqual(4);
    expect(uncoded.sort(), 'a new uncoded Agent Costs reply').toEqual([
      '$"\'{dimension}\' is not something Azure costs can be broken down by."',
      '$"\'{dimension}\' is not something these figures can be broken down by."',
      'ex.Message',
    ]);
    return pairs;
  }

  it('keeps Agent Costs server error codes aligned with SPA mappings and English text', async () => {
    const { AGENT_COST_ERROR_CODE_KEYS } = await import('../../api/agentCostsApi');
    const server = agentCostServerErrors();
    const serverCodes = [...server.keys()].sort();
    const spaCodes = Object.keys(AGENT_COST_ERROR_CODE_KEYS).sort();

    expect(spaCodes, 'SPA maps exactly the AgentCostsAPIController codes it can send').toEqual(serverCodes);

    for (const [code, message] of server) {
      const catalogKey = AGENT_COST_ERROR_CODE_KEYS[code];
      expect(catalogKey, `Missing SPA map entry for Agent Costs error code '${code}'`).toBeTruthy();
      expect(EN_CATALOG[catalogKey], `English catalog for Agent Costs error code '${code}' must match the server`).toBe(message);
    }
  });
});
