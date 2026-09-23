// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

import { EN_CATALOG } from '../catalog';
import { COWORK_TIER_LABEL_KEYS, TENURE_BASIS_LABEL_KEYS } from '../../components/copilotAdoption/serverText';
import { TEAMS_MEETING_BUCKET_LABEL_KEYS, TEAMS_SEGMENT_TEXT_KEYS } from '../../components/teamsExplorer/teamsShared';
import { WEB_ACTIVITY_AVAILABILITY_REASON_KEYS } from '../../components/webActivity/AvailabilityBar';
import { USER_DATA_WORKLOADS_BY_FLAG } from '../../components/userlookup/CategoryRow';
import { ENABLED_IMPORT_LABELS_BY_SETTING_PROPERTY } from '../../pages/InsightsOverviewPage';

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
const CHART_KEY_PROPERTY = /Key\s*=\s*"([^"]+)"/g;
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
      .filter((key) => !expected.has(key));

    expect(
      orphans,
      'These report chart catalog entries are not defined in ReportsAPIController - either the\n' +
        'chart was removed, or its key/field was renamed and the page is now falling back to English.',
    ).toEqual([]);
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
 * Copilot Adoption's Cowork time-saved estimate still carries server-authored assumption strings
 * for API compatibility and for the Excel report, but the SPA renders catalogued wording from the
 * same numeric facts. Keep the counts in lock-step so a future server-side assumption is not
 * silently missing on Spanish pages.
 *
 * The list lives in the time-saved model section, and its facts are the assumptions IN FORCE - the
 * reader's own figures for the session, or the product defaults - not the raw server options. Wiring
 * a sentence to `options.*` would state the defaults beside hours computed from the reader's
 * figures, which is exactly the wrong-value mismatch the third check below exists to catch.
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
const COWORK_TIME_SAVED_MODEL = join(process.cwd(), 'src', 'components', 'copilotAdoption', 'CoworkTimeSavedModel.tsx');
const COPILOT_ADOPTION_SERVER_TEXT_MODULE = join(process.cwd(), 'src', 'components', 'copilotAdoption', 'serverText.ts');
const COWORK_ESTIMATE_ASSUMPTION_CALL = /estimate\.Assumptions\.Add\(/g;
const COWORK_ESTIMATE_ASSUMPTION_PREFIX = 'copilotAdoptionCowork.estimate.assumption.';

function coworkEstimateServerAssumptionCount(): number {
  const source = readFileSync(COPILOT_ADOPTION_SCORING, 'utf8');
  return [...source.matchAll(COWORK_ESTIMATE_ASSUMPTION_CALL)].length;
}

function coworkEstimateRenderedAssumptionKeys(): string[] {
  const source = readFileSync(COWORK_TIME_SAVED_MODEL, 'utf8');
  const list = source.match(/<ul className=\{styles\.assumptionList\}>([\s\S]*?)<\/ul>/)?.[1] ?? '';
  expect(list, 'Could not find the Cowork estimate assumption list').toBeTruthy();

  return sortedUnique(
    [...list.matchAll(/'([^']+)'/g)]
      .map((m) => m[1])
      .filter((key) => key.startsWith(COWORK_ESTIMATE_ASSUMPTION_PREFIX)),
  );
}

function coworkEstimateAssumptionIds(keys: string[]): string[] {
  return sortedUnique(
    keys.map((key) =>
      key.slice(COWORK_ESTIMATE_ASSUMPTION_PREFIX.length).replace(/\.(?:one|other)$/, ''),
    ),
  );
}

describe('Copilot Adoption Cowork estimate assumptions', () => {
  it('finds the scoring file that still authors the compatibility assumptions', () => {
    expect(() => readFileSync(COPILOT_ADOPTION_SCORING, 'utf8')).not.toThrow();
    expect(coworkEstimateServerAssumptionCount()).toBeGreaterThan(0);
    expect(coworkEstimateRenderedAssumptionKeys().length).toBeGreaterThan(0);
  });

  it('renders the same catalogued SPA assumptions the server still authors for compatibility', () => {
    const renderedKeys = coworkEstimateRenderedAssumptionKeys();
    const renderedIds = coworkEstimateAssumptionIds(renderedKeys);
    const catalogIds = coworkEstimateAssumptionIds(catalogKeys(COWORK_ESTIMATE_ASSUMPTION_PREFIX));

    expect(
      { renderedIds, catalogIds, serverCount: coworkEstimateServerAssumptionCount() },
      'CopilotAdoptionScoring added or removed an estimate.Assumptions.Add(...) call. Mirror the\n' +
        'same assumption in CoworkTimeSavedModel.tsx using copilotAdoptionCowork.estimate.assumption.*\n' +
        'catalog entries in en/es, or deliberately remove the obsolete SPA bullet. Plural catalog\n' +
        'forms count as one assumption.',
    ).toEqual({ renderedIds: catalogIds, catalogIds, serverCount: renderedIds.length });
  });

  it('wires each Cowork assumption sentence to the facts that sentence describes', () => {
    const source = readFileSync(COWORK_TIME_SAVED_MODEL, 'utf8');
    const list = source.match(/<ul className=\{styles\.assumptionList\}>([\s\S]*?)<\/ul>/)?.[1] ?? '';

    const requiredFacts: Record<string, string[]> = {
      saves: ['assumptions.meetingMinutes', 'assumptions.emailMinutes', 'assumptions.documentMinutes'],
      volumes: ['projection.cohortUsers', 'projection.workingDaysPerMonth'],
      taskMinutes: ['assumptions.taskMinutes'],
      // The Cowork task rate IN FORCE - the reader's, or the published one - never options.*, and for
      // an observed rate the number of people it averages.
      taskRateObserved: ['projection.cowork.tasksPerPerson', 'projection.cowork.rateUsers'],
      taskRateAssumed: ['projection.cowork.tasksPerPerson'],
      taskRateCustom: ['projection.cowork.tasksPerPerson'],
      overlap: [],
      lowerBound: ['conservativePercent'],
      potential: [],
      notMeasured: [],
      noMoney: [],
    };

    const problems = Object.entries(requiredFacts).flatMap(([id, facts]) => {
      const key = `${COWORK_ESTIMATE_ASSUMPTION_PREFIX}${id}`;
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
      'The Cowork assumption bullets must use the same facts as the server-authored compatibility\n' +
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
