// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

import { EN_CATALOG } from '../catalog';

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
const TEAMS_AVAILABILITY_REASON_CALL = /model\.Reasons\.Add\(/g;

function teamsAvailabilityReasonCount(): number {
  const source = readFileSync(TEAMS_AVAILABILITY_MODEL, 'utf8');
  return [...source.matchAll(TEAMS_AVAILABILITY_REASON_CALL)].length;
}

describe('Teams Explorer availability reasons', () => {
  it('finds the server model that still authors the compatibility reasons', () => {
    expect(() => readFileSync(TEAMS_AVAILABILITY_MODEL, 'utf8')).not.toThrow();
    expect(teamsAvailabilityReasonCount()).toBe(8);
  });

  it('has one catalogued SPA reason for every server-authored availability reason', () => {
    const cataloguedReasons = Object.keys(EN_CATALOG)
      .filter((key) => key.startsWith('teamsExplorer.availability.reason.'));

    expect(
      cataloguedReasons,
      'TeamsExplorerAvailability added or removed a Reasons.Add(...) branch. Mirror the same boolean\n' +
        'condition in AvailabilityBar.tsx and add/remove the matching\n' +
        'teamsExplorer.availability.reason.* catalog entry in en/es.',
    ).toHaveLength(teamsAvailabilityReasonCount());
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
 * for API compatibility, but the SPA renders catalogued wording from the same numeric facts. Keep
 * the counts in lock-step so a future server-side assumption is not silently missing on Spanish
 * pages.
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
const COWORK_PANEL = join(process.cwd(), 'src', 'components', 'copilotAdoption', 'CoworkPanel.tsx');
const COWORK_ESTIMATE_ASSUMPTION_CALL = /estimate\.Assumptions\.Add\(/g;
const COWORK_ESTIMATE_RENDERED_ASSUMPTION = /key="estimate-assumption-[^"]+"/g;

function coworkEstimateServerAssumptionCount(): number {
  const source = readFileSync(COPILOT_ADOPTION_SCORING, 'utf8');
  return [...source.matchAll(COWORK_ESTIMATE_ASSUMPTION_CALL)].length;
}

function coworkEstimateRenderedAssumptionCount(): number {
  const source = readFileSync(COWORK_PANEL, 'utf8');
  return [...source.matchAll(COWORK_ESTIMATE_RENDERED_ASSUMPTION)].length;
}

describe('Copilot Adoption Cowork estimate assumptions', () => {
  it('finds the scoring file that still authors the compatibility assumptions', () => {
    expect(() => readFileSync(COPILOT_ADOPTION_SCORING, 'utf8')).not.toThrow();
    expect(coworkEstimateServerAssumptionCount()).toBeGreaterThan(0);
  });

  it('renders one catalogued SPA bullet for every server-authored assumption', () => {
    expect(
      coworkEstimateRenderedAssumptionCount(),
      'CopilotAdoptionScoring added or removed an estimate.Assumptions.Add(...) call. Mirror the\n' +
        'same assumption in CoworkPanel.tsx using copilotAdoptionCowork.estimate.assumption.*\n' +
        'catalog entries in en/es, or deliberately remove the obsolete SPA bullet.',
    ).toBe(coworkEstimateServerAssumptionCount());
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
const DLP_REASON_CALL = /model\.Reasons\.Add\(/g;

describe('DLP availability reasons', () => {
  it('finds the controller that defines them', () => {
    expect(() => readFileSync(DLP_CONTROLLER, 'utf8')).not.toThrow();
    expect([...readFileSync(DLP_CONTROLLER, 'utf8').matchAll(DLP_REASON_CALL)]).toHaveLength(2);
  });

  it('has one catalogued SPA reason for every server-authored DLP availability reason', () => {
    const cataloguedReasons = Object.keys(EN_CATALOG).filter((key) => key.startsWith('dlp.availability.reason.'));
    expect(cataloguedReasons).toHaveLength([...readFileSync(DLP_CONTROLLER, 'utf8').matchAll(DLP_REASON_CALL)].length);
  });
});

/** Agent cost availability messages are server-authored in SqlAgentCostReportStore.AddMessages. */
const AGENT_COST_STORE = join(process.cwd(), '..', '..', '..', 'Common', 'Entities', 'AgentCosts', 'SqlAgentCostReportStore.cs');
const AGENT_COST_MESSAGE_CALL = /result\.Messages\.Add\(/g;

describe('Agent cost availability messages', () => {
  it('finds the store that defines them', () => {
    expect(() => readFileSync(AGENT_COST_STORE, 'utf8')).not.toThrow();
    expect([...readFileSync(AGENT_COST_STORE, 'utf8').matchAll(AGENT_COST_MESSAGE_CALL)].length).toBeGreaterThanOrEqual(10);
  });

  it('has one catalogued SPA message for every server-authored availability message', () => {
    const cataloguedMessages = Object.keys(EN_CATALOG).filter((key) => key.startsWith('agentCosts.availability.message.'));
    expect(cataloguedMessages).toHaveLength([...readFileSync(AGENT_COST_STORE, 'utf8').matchAll(AGENT_COST_MESSAGE_CALL)].length);
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

describe('Copilot Adoption server-authored text', () => {
    it('finds the C# files that define it', () => {
      expect(() => readFileSync(COPILOT_ADOPTION_SCORING_TEXT, 'utf8')).not.toThrow();
      expect(() => readFileSync(COPILOT_ADOPTION_SERVICE_TEXT, 'utf8')).not.toThrow();
      expect(() => readFileSync(COPILOT_ADOPTION_CONTROLLER_TEXT, 'utf8')).not.toThrow();
      expect(copilotAdoptionScoringSource()).toContain('public static string ActionLabel');
      expect(copilotAdoptionServiceSource()).toContain('BuildFunnel');
    });

    it('translates every recommended-action code the scorer can send', () => {
      const source = copilotAdoptionScoringSource();
      const codes = [...new Set([...source.matchAll(/public const string \w+ = "([^"]+)";/g)]
        .map((m) => m[1])
        .filter((code) => ['reclaim', 'reengage', 'coach', 'broaden', 'grow', 'sustain', 'advocate', 'review', 'excluded'].includes(code)))];
      expect(codes.length).toBe(9);

      const missing = codes.flatMap((code) => [
        `copilotAdoption.server.action.${code}.label`,
        `copilotAdoption.server.action.${code}.description`,
      ]).filter((key) => !(key in EN_CATALOG));

      expect(missing).toEqual([]);
    });

    it('translates every engagement band and habit bucket label the scorer can send', () => {
      const source = copilotAdoptionScoringSource();
      const bandLabels = [...source.matchAll(/case AdoptionBand\.\w+: return "([^"]+)";/g)].map((m) => m[1]);
      const bucketLabels = [...source.matchAll(/"((?:Infrequent|Moderate|Frequent|Daily))"/g)].map((m) => m[1]);

      expect(new Set(bandLabels).size).toBeGreaterThanOrEqual(6);
      expect(new Set(bucketLabels).size).toBe(4);

      const expected = [
        'copilotAdoption.server.band.neverUsed',
        'copilotAdoption.server.band.dormant',
        'copilotAdoption.server.band.trialling',
        'copilotAdoption.server.band.developing',
        'copilotAdoption.server.band.established',
        'copilotAdoption.server.band.champion',
        'copilotAdoption.server.habitBucket.infrequent',
        'copilotAdoption.server.habitBucket.moderate',
        'copilotAdoption.server.habitBucket.frequent',
        'copilotAdoption.server.habitBucket.daily',
        'copilotAdoption.server.habitBucket.infrequent.range',
        'copilotAdoption.server.habitBucket.moderate.range',
        'copilotAdoption.server.habitBucket.frequent.range',
        'copilotAdoption.server.habitBucket.daily.range',
      ];

      expect(expected.filter((key) => !(key in EN_CATALOG))).toEqual([]);
    });

    it('translates every funnel, concentration and profile label the service can send', () => {
      const source = copilotAdoptionServiceSource();
      const serviceLabels = [
        ...source.matchAll(/new AdoptionCategory \{ Label = "([^"]+)"/g),
        ...source.matchAll(/Profile\("([^"]+)"/g),
      ].map((m) => m[1]);

      expect(serviceLabels).toEqual(expect.arrayContaining(['Licensed', 'Ever used Copilot', 'Typical active user']));

      const expected = [
        'copilotAdoption.server.funnel.licensed',
        'copilotAdoption.server.funnel.everUsedCopilot',
        'copilotAdoption.server.funnel.activeThisPeriod',
        'copilotAdoption.server.funnel.habitualUsers',
        'copilotAdoption.server.funnel.champions',
        'copilotAdoption.server.scoreProfile.typicalActiveUser',
        'copilotAdoption.server.scoreProfile.yourChampions',
        'copilotAdoption.server.concentration.top10',
        'copilotAdoption.server.concentration.next15',
        'copilotAdoption.server.concentration.next25',
        'copilotAdoption.server.concentration.bottom50',
      ];

      expect(expected.filter((key) => !(key in EN_CATALOG))).toEqual([]);
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
