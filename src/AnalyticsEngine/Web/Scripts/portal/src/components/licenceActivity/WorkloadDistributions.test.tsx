import { describe, it, expect } from 'vitest';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../../test/renderWithProvider';
import WorkloadDistributions from './WorkloadDistributions';
import type { LicenceActivityDistribution, WorkloadKey } from '../../types/licenceActivity';
import { BAND_DESCRIPTIONS, COPILOT_COVERAGE_NOTE } from './bands';

function dist(workload: WorkloadKey, over: Partial<LicenceActivityDistribution> = {}): LicenceActivityDistribution {
  return { workload, high: 0, moderate: 0, low: 0, zero: 0, unknown: 0, ...over };
}

describe('WorkloadDistributions', () => {
  it('summarises active over the MEASURED population and shows a measured zero band', () => {
    renderWithProvider(<WorkloadDistributions workloads={[dist('teams', { high: 5, zero: 3 })]} />);
    // measured = 5 + 3 = 8, active = 5 -> 62.5%
    expect(screen.getByText(/5 of 8 active/)).toBeInTheDocument();
    expect(screen.getByText(/62\.5%/)).toBeInTheDocument();
    // "No activity" (measured zero) is a real, rendered band - not merged with Unknown.
    expect(screen.getByText('No activity 3')).toBeInTheDocument();
    expect(screen.getByTitle(`No activity: 3. ${BAND_DESCRIPTIONS.zero}`)).toBeInTheDocument();
    expect(screen.getByRole('note')).toHaveTextContent('Unknown means insufficient data, not no activity.');
    expect(screen.queryByText(COPILOT_COVERAGE_NOTE)).not.toBeInTheDocument();
  });

  it('renders a fully-unmeasured workload as "Not measured", never "0 active"', async () => {
    const user = userEvent.setup();
    renderWithProvider(<WorkloadDistributions workloads={[dist('copilot', { unknown: 10 })]} />);
    expect(screen.getByText('Not measured')).toBeInTheDocument();
    expect(screen.getByText('Unknown 10')).toBeInTheDocument();
    // The card's summary line is `N of M active (P%)`. Match that shape specifically: a looser
    // /of .* active/ also matches the explanatory method note, which legitimately contains both words.
    expect(screen.queryByText(/\d+ of \d+ active/)).not.toBeInTheDocument();
    expect(screen.getByTitle(`Unknown: 10. ${BAND_DESCRIPTIONS.unknown}`)).toBeInTheDocument();
    await user.click(screen.getByText('Why is activity Unknown?'));
    expect(screen.getByText(COPILOT_COVERAGE_NOTE)).toBeVisible();
  });

  it('puts help directly below each service legend and scopes the Copilot explanation to Copilot', async () => {
    const user = userEvent.setup();
    renderWithProvider(
      <WorkloadDistributions workloads={[dist('teams', { zero: 5 }), dist('copilot', { unknown: 5 })]} />,
    );

    for (const label of ['No activity 5', 'Unknown 5']) {
      const legend = screen.getByText(label).parentElement?.parentElement;
      const help = legend?.nextElementSibling;
      expect(help).toBeInstanceOf(HTMLElement);
      if (!(help instanceof HTMLElement)) throw new Error('Expected help immediately after the service legend');
      expect(within(help).getByRole('note')).toHaveTextContent(
        'Unknown means insufficient data, not no activity. No activity means complete reporting data shows no usage.',
      );
      await user.click(within(help).getByText('Why is activity Unknown?'));
      if (label === 'Unknown 5') {
        expect(within(help).getByText(COPILOT_COVERAGE_NOTE)).toBeVisible();
      } else {
        expect(within(help).queryByText(COPILOT_COVERAGE_NOTE)).not.toBeInTheDocument();
      }
    }
  });
});
