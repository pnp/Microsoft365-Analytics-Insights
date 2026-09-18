// Mirrors Common/Entities/SpoWebActivity/*.cs (returned by api/WebActivity).
//
// The server serialises these with a CamelCaseNamingStrategy, so every field here is camelCase.
// The Web project has no global camelCase resolver - each model opts in with
// [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))] - so if a new server property
// ever renders PascalCase, this file is where the mismatch will show up as `undefined`.

/** One executed query, so a section can show its SQL and report its own failure. */
export interface WebActivityQueryInfo {
  key: string;
  sql: string;
  error: string | null;
  elapsedMs: number;
}

/** The reporting window every tab is built from. */
export interface WebActivityWindow {
  days: number;
  fromUtc: string;
  toUtc: string;
  workingDays: number;
  top: number;
  minimumViews: number;
}

/** A verdict on one headline figure. */
export interface WebActivityJudgement {
  key: string;
  tone: 'good' | 'neutral' | 'warning' | 'critical';
  headline: string;
  detail: string;
}

/** A named value in a ranked list. */
export interface WebActivityNamedCount {
  name: string;
  count: number;
  sharePct: number | null;
}

/** One bucket of a distribution. */
export interface WebActivityBucket {
  key: string;
  label: string;
  count: number;
  sharePct: number;
}

/** One point of a weekly series. */
export interface WebActivityTrendPoint {
  weekStart: string;
  pageViews: number;
  visits: number;
  visitors: number;
  bounces: number;
  searches: number;
}

/** A weekly value for one named series, for the stacked charts. */
export interface WebActivityStackPoint {
  weekStart: string;
  name: string;
  count: number;
}

/** One cell of the day-of-week x hour-of-day grid (UTC). */
export interface WebActivityHeatCell {
  day: number;
  hour: number;
  pageViews: number;
  visits: number;
}

/** Fields every section carries. */
export interface WebActivitySection {
  window: WebActivityWindow;
  queries: WebActivityQueryInfo[];
}

/** What this deployment can report on, and why anything missing is missing. */
export interface WebActivityAvailability {
  webTrafficAvailable: boolean;
  userMetadataAvailable: boolean;
  appInsightsConfigured: boolean;
  hasAnyHits: boolean;
  lastHitUtc: string | null;
  searchAvailable: boolean;
  clickTrackingAvailable: boolean;
  available: boolean;
  reasons: string[];
}

export interface WebActivityOverviewKpis {
  pageViews: number;
  uniquePageViews: number;
  visits: number;
  visitors: number;
  knownUsers: number;
  reachPct: number;
  uniquePages: number;
  sites: number;
  pagesPerVisit: number;
  bouncePct: number;
  averageSecondsOnPage: number;
  averageLoadSeconds: number;
  newVisitors: number;
  returningVisitors: number;
  mobileVisitPct: number;
}

export interface WebActivityOverview extends WebActivitySection {
  kpis: WebActivityOverviewKpis;
  trend: WebActivityTrendPoint[];
  visitorSegments: WebActivityBucket[];
  visitDepth: WebActivityBucket[];
  heatmap: WebActivityHeatCell[];
  topSites: WebActivityNamedCount[];
  judgements: WebActivityJudgement[];
}

export interface WebActivityVisitKpis {
  visits: number;
  visitors: number;
  uniquePages: number;
  visitsPerVisitor: number;
  earliestVisitHour: number | null;
  latestVisitHour: number | null;
  outOfHoursVisits: number;
  outOfHoursPct: number;
}

export interface WebActivityVisits extends WebActivitySection {
  kpis: WebActivityVisitKpis;
  trend: WebActivityTrendPoint[];
  bySite: WebActivityNamedCount[];
  byPage: WebActivityNamedCount[];
  byDevice: WebActivityNamedCount[];
  byBrowser: WebActivityNamedCount[];
  byDay: WebActivityBucket[];
  byPeriodOfDay: WebActivityBucket[];
  byHour: WebActivityBucket[];
  siteOverTime: WebActivityStackPoint[];
}

/** A page and everything the report knows about how it performed. */
export interface WebActivityPageRow {
  title: string;
  url: string;
  site: string | null;
  pageViews: number;
  uniquePageViews: number;
  visitors: number;
  averageSecondsOnPage: number | null;
  averageLoadSeconds: number | null;
  entries: number;
  exits: number;
  bounces: number;
  bouncePct: number | null;
}

export interface WebActivitySiteRow {
  name: string;
  url: string | null;
  pageViews: number;
  uniquePageViews: number;
  visits: number;
  visitors: number;
}

export interface WebActivityPageKpis {
  pageViews: number;
  uniquePageViews: number;
  uniqueSharePct: number;
  pagesPerVisit: number;
  averageSecondsOnPage: number;
  averageLoadSeconds: number;
  uniquePages: number;
  quietPages: number;
  topDecilePagePct: number;
}

export interface WebActivityPages extends WebActivitySection {
  kpis: WebActivityPageKpis;
  topPages: WebActivityPageRow[];
  slowestPages: WebActivityPageRow[];
  quietPages: WebActivityPageRow[];
  bySite: WebActivitySiteRow[];
  periodOverTime: WebActivityStackPoint[];
}

export interface WebActivityTransitionRow {
  fromTitle: string;
  fromUrl: string;
  toTitle: string;
  toUrl: string;
  count: number;
  sharePct: number;
}

export interface WebActivityJourneyKpis {
  visits: number;
  bounces: number;
  bouncePct: number;
  pagesPerVisit: number;
  medianPagesPerVisit: number;
  averageVisitSeconds: number;
  clicks: number;
}

export interface WebActivityJourneys extends WebActivitySection {
  kpis: WebActivityJourneyKpis;
  entryPages: WebActivityPageRow[];
  exitPages: WebActivityPageRow[];
  bouncePages: WebActivityPageRow[];
  transitions: WebActivityTransitionRow[];
  depth: WebActivityBucket[];
  clickedElements: WebActivityNamedCount[];
}

export interface WebActivityGeographyKpis {
  visits: number;
  visitors: number;
  countries: number;
  cities: number;
  provinces: number;
  unknownLocationPageViews: number;
  unknownLocationPct: number;
}

export interface WebActivityPlaceRow {
  name: string;
  country: string | null;
  pageViews: number;
  visits: number;
  visitors: number;
  sharePct: number;
}

export interface WebActivityGeography extends WebActivitySection {
  kpis: WebActivityGeographyKpis;
  countries: WebActivityPlaceRow[];
  cities: WebActivityPlaceRow[];
  provinces: WebActivityPlaceRow[];
  countryOverTime: WebActivityStackPoint[];
}

export interface WebActivitySearchKpis {
  searches: number;
  terms: number;
  searchers: number;
  sessionsWithSearch: number;
  searchReliancePct: number;
  searchesPerSearchingVisit: number;
  strugglingVisits: number;
  deadEndSearches: number;
  deadEndPct: number;
}

export interface WebActivitySearchTermRow {
  term: string;
  searches: number;
  searchers: number;
  deadEnds: number;
  deadEndPct: number;
}

export interface WebActivitySearch extends WebActivitySection {
  kpis: WebActivitySearchKpis;
  topTerms: WebActivitySearchTermRow[];
  deadEndTerms: WebActivitySearchTermRow[];
  trend: WebActivityTrendPoint[];
  byDay: WebActivityBucket[];
  byPeriodOfDay: WebActivityBucket[];
  bySite: WebActivityNamedCount[];
}

export interface WebActivityTechnologyKpis {
  browsers: number;
  operatingSystems: number;
  devices: number;
  mobilePct: number;
  averageLoadSeconds: number;
  p95LoadSeconds: number;
  unknownBrowserPageViews: number;
}

export interface WebActivityPlatformRow {
  name: string;
  pageViews: number;
  visits: number;
  visitors: number;
  sharePct: number;
  averageLoadSeconds: number | null;
  averageSecondsOnPage: number | null;
}

export interface WebActivityTechnologyDetailRow {
  browser: string;
  device: string;
  operatingSystem: string;
  city: string;
  visits: number;
  visitors: number;
  pageViews: number;
  pageViewsPerVisit: number;
  averageSecondsOnPage: number | null;
  averageLoadSeconds: number | null;
}

export interface WebActivityTechnology extends WebActivitySection {
  kpis: WebActivityTechnologyKpis;
  browsers: WebActivityPlatformRow[];
  operatingSystems: WebActivityPlatformRow[];
  devices: WebActivityPlatformRow[];
  deviceOverTime: WebActivityStackPoint[];
  detail: WebActivityTechnologyDetailRow[];
}

/** The CSV exports the API offers. Must match `WebActivityExports.Sections` on the server. */
export type WebActivityExportSection =
  | 'pages'
  | 'quiet-pages'
  | 'slow-pages'
  | 'entry-pages'
  | 'exit-pages'
  | 'transitions'
  | 'search-terms'
  | 'technology';
