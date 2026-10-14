import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import { loadCatalog } from '../../i18n';
import { renderWithProvider } from '../../test/renderWithProvider';
import { ScoreSettingsBanner, ScoreSettingsMethodNote, effectiveScoreValues } from './ScoreSettingsNotice';
import type { CopilotAdoptionOptions } from '../../types/copilotAdoption';
import type { CopilotAdoptionScoreValues } from '../../types/copilotAdoptionSettings';

/** Only the fields the notice reads matter; the rest of the options are irrelevant here. */
const DEFAULT_OPTIONS = {
  windowDays: 45,
  historyDays: 200,
  workingDaysPerWeek: 5,
  frequencyTargetRatio: 0.6,
  depthTargetInteractionsPerActiveDay: 5,
  breadthTargetApps: 3,
  frequencyWeight: 50,
  depthWeight: 30,
  breadthWeight: 20,
  championScore: 75,
  establishedScore: 50,
  developingScore: 25,
  habitBucketNormalisationDays: 28,
  habitModerateMinDays: 4,
  habitFrequentMinDays: 8,
  habitDailyMinDays: 16,
  agentReviewInactiveDays: 30,
  agentRetireInactiveDays: 60,
  agentNewDays: 14,
  agentMinUsers: 3,
  agentHistoryDays: 120,
  opportunityUnlicensedCopilotWeight: 40,
  opportunityCollaborationWeight: 25,
  opportunityEmailWeight: 20,
  opportunityDocumentWeight: 15,
  opportunityCopilotTarget: 20,
  opportunityCollaborationTarget: 30,
  opportunityEmailTarget: 40,
  opportunityDocumentTarget: 20,
  opportunityRecommendScore: 60,
  usageReportLagDays: 3,
  topSegments: 10,
  minSeatsPerSegment: 5,
  maxLicensedUsersScored: 50000,
  maxOpportunityCandidates: 50000,
  maxAgents: 1000,
  maxUnlicensedUsersScored: 50000,
} as CopilotAdoptionOptions;

const DEFAULTS: CopilotAdoptionScoreValues = {
  frequencyWeightPercent: 50, depthWeightPercent: 30, breadthWeightPercent: 20,
  developingScore: 25, establishedScore: 50, championScore: 75,
};

const DEFAULT_REPORT: CopilotAdoptionOptions = {
  ...DEFAULT_OPTIONS,
  scoreSettings: { version: 0, customised: false, customisedFields: [], updatedUtc: null, defaults: DEFAULTS },
};

/** What the server sends once an administrator has saved 60/20/20 and Champion from 90. */
const CUSTOM_REPORT: CopilotAdoptionOptions = {
  ...DEFAULT_OPTIONS,
  frequencyWeight: 60,
  depthWeight: 20,
  breadthWeight: 20,
  championScore: 90,
  scoreSettings: {
    version: 3,
    customised: true,
    customisedFields: ['frequencyWeightPercent', 'depthWeightPercent', 'championScore'],
    updatedUtc: '2026-10-01T09:00:00Z',
    defaults: DEFAULTS,
  },
};

describe('ScoreSettingsNotice', () => {
  it('reads the effective values back as whole percentages', () => {
    expect(effectiveScoreValues(CUSTOM_REPORT)).toEqual({ ...DEFAULTS, frequencyWeightPercent: 60, depthWeightPercent: 20, championScore: 90 });
    expect(effectiveScoreValues({ ...DEFAULT_OPTIONS, frequencyWeight: 0.5, depthWeight: 0.3, breadthWeight: 0.2 })).toEqual(DEFAULTS);
  });

  it('shows no banner for the default settings, or for a server that does not report them', () => {
    const { container, rerender } = renderWithProvider(<ScoreSettingsBanner options={DEFAULT_REPORT} />);
    expect(container.textContent).toBe('');
    rerender(<ScoreSettingsBanner options={DEFAULT_OPTIONS} />);
    expect(container.textContent).toBe('');
  });

  it('states the customised values, the defaults and how to reset them', () => {
    renderWithProvider(<ScoreSettingsBanner options={CUSTOM_REPORT} />);
    const banner = screen.getByTestId('score-settings-banner');
    expect(banner.textContent).toContain('Customised score settings.');
    expect(banner.textContent).toContain('This report uses weights frequency 60%, depth 20%, breadth 20%; levels Developing from 25, Established from 50, Champion from 90.');
    expect(banner.textContent).toContain('The defaults are weights frequency 50%, depth 30%, breadth 20%; levels Developing from 25, Established from 50, Champion from 75.');
    expect(banner.textContent).toContain('Reset to defaults');
  });

  it('documents the default settings on the Method tab', () => {
    renderWithProvider(<ScoreSettingsMethodNote options={DEFAULT_REPORT} />);
    expect(screen.getByTestId('score-settings-method-note').textContent).toBe(
      'These are the default score settings: weights frequency 50%, depth 30%, breadth 20%; levels Developing from 25, Established from 50, Champion from 75. Administrators can change the weights and level thresholds under Administration > Copilot Adoption settings.');
  });

  it('documents customised settings on the Method tab', () => {
    renderWithProvider(<ScoreSettingsMethodNote options={CUSTOM_REPORT} />);
    expect(screen.getByTestId('score-settings-method-note').textContent).toContain('Champion from 90');
    expect(screen.getByTestId('score-settings-method-note').textContent).toContain('Reset to defaults');
  });

  it('renders in Spanish', async () => {
    await loadCatalog('es');
    renderWithProvider(<ScoreSettingsBanner options={CUSTOM_REPORT} />, { language: 'es' });
    const text = screen.getByTestId('score-settings-banner').textContent ?? '';
    expect(text).toContain('Configuración de puntuación personalizada.');
    expect(text).toContain('frecuencia 60 %');
    expect(text).toContain('Campeón desde 90');
    expect(text).toContain('Restablecer valores predeterminados');
  });
});