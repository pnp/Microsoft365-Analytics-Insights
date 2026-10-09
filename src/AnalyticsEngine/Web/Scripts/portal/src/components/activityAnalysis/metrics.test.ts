import { beforeEach, describe, expect, it } from 'vitest';
import { loadCatalog, translateActive, type TFunction } from '../../i18n';
import { setActiveLanguage } from '../../i18n/runtime';
import type { ActivityAnalysisMetric } from '../../types/activityAnalysis';
import {
  DEFAULT_METRICS,
  categoryCheckState,
  categoryLabel,
  defaultSelection,
  formatMetricValue,
  groupMetrics,
  metricLabel,
  metricLabelWithUnit,
  metricOrder,
  orderedSelection,
  toggleCategory,
  toggleMetric,
} from './metrics';

const t: TFunction = (key, values) => translateActive(key, values);

function metric(key: string, category: string, overrides: Partial<ActivityAnalysisMetric> = {}): ActivityAnalysisMetric {
  return { key, category, unit: 'count', core: false, available: true, label: key, ...overrides };
}

const TEAMS_CORE = DEFAULT_METRICS.map((key) => metric(key, 'teams', { core: true }));
const METRICS: ActivityAnalysisMetric[] = [
  ...TEAMS_CORE,
  metric('teams.audioDuration', 'teams', { unit: 'seconds' }),
  metric('outlook.emailsSent', 'outlook', { core: true }),
  metric('outlook.emailsRead', 'outlook', { core: true, available: false }),
  metric('copilot.chats', 'copilot', { core: true }),
  metric('copilot.app.word', 'copilot'),
  metric('vivaEngage.posted', 'vivaEngage', { core: true, available: false }),
];
const CATEGORIES = ['teams', 'outlook', 'onedrive', 'sharepoint', 'copilot', 'vivaEngage'];

beforeEach(() => {
  setActiveLanguage('en');
});

describe('metric labels', () => {
  it('translates every metric key the API defines', async () => {
    expect(metricLabel(t, 'teams.privateChats', 'Teams Private Chats')).toBe('Teams private chats');
    expect(metricLabel(t, 'copilot.app.word', 'Copilot App Word')).toBe('Copilot in Word');

    await loadCatalog('es');
    setActiveLanguage('es');
    expect(metricLabel(t, 'teams.privateChats', 'Teams Private Chats')).toBe('Chats privados de Teams');
    expect(metricLabel(t, 'copilot.app.word', 'Copilot App Word')).toBe('Copilot en Word');
  });

  it('falls back to the server’s English label for a metric this build does not know, then to its key', async () => {
    await loadCatalog('es');
    setActiveLanguage('es');
    expect(metricLabel(t, 'teams.holograms', 'Teams Holograms')).toBe('Teams Holograms');
    expect(metricLabel(t, 'teams.holograms', '')).toBe('teams.holograms');
    expect(metricLabel(t, 'teams.holograms')).toBe('teams.holograms');
  });

  it('does not mistake another key in the module for a metric', () => {
    // `activityAnalysis.unit.withHours` lives beside the labels; only `metric.<key>` names a metric.
    expect(metricLabel(t, 'withHours', 'With hours')).toBe('With hours');
  });

  it('names durations in hours, and categories by their product name', () => {
    expect(metricLabelWithUnit(t, 'teams.audioDuration', metric('teams.audioDuration', 'teams', { unit: 'seconds' }))).toBe(
      'Teams audio time (hours)',
    );
    expect(metricLabelWithUnit(t, 'teams.calls', metric('teams.calls', 'teams'))).toBe('Teams calls');
    expect(categoryLabel(t, 'vivaEngage')).toBe('Viva Engage');
    expect(categoryLabel(t, 'holograms')).toBe('holograms');
  });

  it('shows a duration in hours and a count as a whole number', () => {
    expect(formatMetricValue({ unit: 'seconds' }, 5400)).toBe('1.5');
    expect(formatMetricValue({ unit: 'count' }, 1234)).toBe('1,234');
  });
});

describe('metric groups and the default selection', () => {
  it('groups the available metrics by category, in the server’s order, dropping empty categories', () => {
    const groups = groupMetrics({ categories: CATEGORIES, metrics: METRICS });
    expect(groups.map((g) => g.category)).toEqual(['teams', 'outlook', 'copilot']);
    expect(groups[1].metrics.map((m) => m.key)).toEqual(['outlook.emailsSent']);
  });

  it('keeps a category the server did not list, rather than dropping its metrics', () => {
    const groups = groupMetrics({ categories: ['teams'], metrics: [...TEAMS_CORE, metric('loop.pages', 'loop')] });
    expect(groups.map((g) => g.category)).toEqual(['teams', 'loop']);
  });

  it('opens on the core Teams metrics', () => {
    const groups = groupMetrics({ categories: CATEGORIES, metrics: METRICS });
    expect(defaultSelection(groups)).toEqual([...DEFAULT_METRICS]);
  });

  it('falls back to the first category’s core metrics, then to the first metric', () => {
    const noTeams = groupMetrics({ categories: CATEGORIES, metrics: METRICS.filter((m) => m.category !== 'teams') });
    expect(defaultSelection(noTeams)).toEqual(['outlook.emailsSent']);

    const noCore = groupMetrics({ categories: CATEGORIES, metrics: [metric('copilot.app.word', 'copilot')] });
    expect(defaultSelection(noCore)).toEqual(['copilot.app.word']);
  });
});

describe('metric slicer selection', () => {
  const groups = groupMetrics({ categories: CATEGORIES, metrics: METRICS });
  const order = metricOrder(groups);
  const teams = groups[0];
  const copilot = groups[2];

  it('shows a category as ticked, empty or mixed', () => {
    expect(categoryCheckState([...DEFAULT_METRICS, 'teams.audioDuration'], teams)).toBe(true);
    expect(categoryCheckState(['teams.calls'], teams)).toBe('mixed');
    expect(categoryCheckState(['copilot.chats'], teams)).toBe(false);
  });

  it('completes a mixed category, fills an empty one and clears a full one', () => {
    const mixed = toggleCategory(['teams.calls', 'copilot.chats'], teams, order);
    expect(mixed).toEqual([...DEFAULT_METRICS, 'teams.audioDuration', 'copilot.chats']);

    const filled = toggleCategory(['outlook.emailsSent'], copilot, order);
    expect(filled).toEqual(['outlook.emailsSent', 'copilot.chats', 'copilot.app.word']);

    const cleared = toggleCategory(mixed, teams, order);
    expect(cleared).toEqual(['copilot.chats']);
  });

  it('toggles one metric and keeps the selection in catalogue order however it was ticked', () => {
    const added = toggleMetric(['copilot.chats'], 'teams.calls', order);
    expect(added).toEqual(['teams.calls', 'copilot.chats']);
    expect(toggleMetric(added, 'copilot.chats', order)).toEqual(['teams.calls']);
  });

  it('drops keys the catalogue does not offer', () => {
    expect(orderedSelection(['outlook.emailsRead', 'teams.calls', 'nonsense'], order)).toEqual(['teams.calls']);
  });
});
