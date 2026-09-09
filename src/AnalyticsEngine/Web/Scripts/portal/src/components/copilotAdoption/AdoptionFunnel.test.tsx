import { describe, it, expect } from 'vitest';
import { renderWithProvider } from '../../test/renderWithProvider';
import AdoptionFunnel from './AdoptionFunnel';
import type { CopilotAdoptionOptions } from '../../types/copilotAdoption';
import type { ReportCategory } from '../../types/reports';

/** Deliberately non-default thresholds, so a hard-coded number in a tooltip cannot pass by luck. */
const OPTIONS: CopilotAdoptionOptions = {
  windowDays: 45,
  historyDays: 200,
  workingDaysPerWeek: 5,
  frequencyTargetRatio: 0.6,
  depthTargetInteractionsPerActiveDay: 5,
  breadthTargetApps: 3,
  frequencyWeight: 40,
  depthWeight: 35,
  breadthWeight: 25,
  championScore: 71,
  establishedScore: 47,
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
};

/** The stage labels exactly as the server's BuildFunnel emits them. */
const STAGES: ReportCategory[] = [
  { label: 'Licensed', value: 600 },
  { label: 'Ever used Copilot', value: 511 },
  { label: 'Active this period', value: 426 },
  { label: 'Habitual users', value: 169 },
  { label: 'Champions', value: 67 },
];

/**
 * The stage-name tooltips are the whole point of the left gutter: a reader who did not choose the
 * wording needs to know what rule decides membership. They live in SVG title elements, which
 * testing-library reaches by title text.
 */
function stageTooltips(container: HTMLElement): string[] {
  return Array.from(container.querySelectorAll('rect > title')).map(t => t.textContent ?? '');
}

function tooltipFor(container: HTMLElement, label: string): string | undefined {
  return stageTooltips(container).find(text => text.startsWith(label));
}

describe('AdoptionFunnel stage tooltips', () => {
  it('explains how every stage is counted, using the live thresholds', () => {
    const { container } = renderWithProvider(<AdoptionFunnel stages={STAGES} options={OPTIONS} />);

    expect(tooltipFor(container, 'Licensed')).toContain('Copilot licence SKU');

    // "Ever used" is NOT bounded by the history window (the usage report's last-activity date is
    // unclamped), and absence does NOT prove no activity (a report row with a last-activity date but
    // null counters bands the user NeverUsed). So the tooltip states the stage's exact composition -
    // active-in-period plus Dormant - rather than describing the evidence that qualifies a user.
    const everUsed = tooltipFor(container, 'Ever used Copilot');
    expect(everUsed).toContain('at least 200 days');
    expect(everUsed).toContain('banded Dormant');
    expect(everUsed).not.toMatch(/anyone missing|no Copilot activity|no Copilot use|bounded by|any evidence/i);

    const active = tooltipFor(container, 'Active this period');
    expect(active).toContain('at least one Copilot interaction inside the selected reporting period');
    expect(active).toContain('(45 days)');
    // The usage-report fallback covers Microsoft's window rather than the selected one.
    expect(active).toContain('per-user usage report');
    // Dormant is a classification from the available signals, not proof that anyone stopped.
    expect(active).not.toMatch(/then stopped/i);

    expect(tooltipFor(container, 'Habitual users')).toContain('reaches 47 out of 100');

    expect(tooltipFor(container, 'Champions')).toContain('reaches 71 out of 100');
  });

  it('never claims a single score component cannot reach the habit threshold', () => {
    const { container } = renderWithProvider(<AdoptionFunnel stages={STAGES} options={OPTIONS} />);

    // With the shipped weights (frequency 0.5) and establishedScore 50, full marks on frequency
    // alone scores exactly 50, so any "no one component can get there alone" wording is false.
    // The toContain is what stops this passing vacuously if the tooltip disappears entirely.
    const habitual = tooltipFor(container, 'Habitual users');
    expect(habitual).toContain('reaches 47 out of 100');
    expect(habitual).not.toMatch(/on its own|alone|no single/i);
  });

  it('says nothing rather than the wrong thing when the server renames a stage', () => {
    const { container } = renderWithProvider(
      <AdoptionFunnel stages={[{ label: 'Some new stage', value: 10 }]} options={OPTIONS} />,
    );

    // A single-stage funnel has no drop-off column, so the only rect tooltips it could produce are
    // the stage-name ones. An unrecognised label must not inherit a neighbouring stage's definition.
    expect(stageTooltips(container)).toHaveLength(0);
  });
});
