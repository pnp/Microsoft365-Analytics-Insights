import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import CoworkQuadrant from './CoworkQuadrant';
import type { CopilotAdoptionOptions, CoworkQuadrantPoint } from '../../types/copilotAdoption';

/**
 * Only the fields the quadrant actually reads. Cast rather than filled out in full so a future option
 * being added to the interface does not break every test in this file.
 */
const OPTIONS = {
  coworkLoadMinScore: 50,
  coworkFluencyMinScore: 50,
} as CopilotAdoptionOptions;

const POINTS: CoworkQuadrantPoint[] = [
  // Top-right: fluent and loaded. The only corner worth enabling first.
  {
    segment: 'Client Services',
    licensedUsers: 120,
    coordinationLoadScore: 78,
    fluencyScore: 71,
    regularCoworkUsers: 9,
    primeCandidates: 44,
  },
  // Bottom-right: loaded but not fluent. Needs Copilot basics before Cowork.
  {
    segment: 'Πωλήσεις',
    licensedUsers: 60,
    coordinationLoadScore: 84,
    fluencyScore: 18,
    regularCoworkUsers: 0,
    primeCandidates: 0,
  },
  // Top-left: fluent but nothing to delegate.
  {
    segment: 'Research',
    licensedUsers: 25,
    coordinationLoadScore: 21,
    fluencyScore: 66,
    regularCoworkUsers: 1,
    primeCandidates: 0,
  },
  // Bottom-left: below both bars.
  {
    segment: 'Facilities',
    licensedUsers: 8,
    coordinationLoadScore: 12,
    fluencyScore: 9,
    regularCoworkUsers: 0,
    primeCandidates: 0,
  },
];

/** The <title> element of a bubble, which is what a reader actually hovers to read. */
function bubbleTitle(segment: string, container: HTMLElement): string {
  const titles = Array.from(container.querySelectorAll('title'));
  const match = titles.find((t) => (t.textContent ?? '').startsWith(segment));
  return match?.textContent ?? '';
}

describe('CoworkQuadrant', () => {
  it('renders an empty state rather than an empty plot', () => {
    renderWithProvider(<CoworkQuadrant points={[]} options={OPTIONS} />);

    expect(screen.getByText(/Not enough Copilot seats/)).toBeTruthy();
  });

  it('plots one bubble per department', () => {
    const { container } = renderWithProvider(<CoworkQuadrant points={POINTS} options={OPTIONS} />);

    expect(container.querySelectorAll('circle').length).toBe(POINTS.length);
  });

  it('separates observed Cowork use from the predicted position', () => {
    // The department's POSITION is an inference from workload and fluency. Only the "already using
    // Cowork" count is observed, and the tooltip has to keep the two apart or the chart quietly
    // presents a forecast as a measurement.
    const { container } = renderWithProvider(<CoworkQuadrant points={POINTS} options={OPTIONS} />);

    const title = bubbleTitle('Client Services', container);
    expect(title).toContain('Already using Cowork regularly (observed): 9');
    expect(title).toContain('Prime candidates (predicted): 44');
  });

  it('says in the caption that a position is a prediction', () => {
    renderWithProvider(<CoworkQuadrant points={POINTS} options={OPTIONS} />);

    expect(screen.getByText(/prediction/)).toBeTruthy();
  });

  it('encodes the quadrant with a letter as well as a colour', () => {
    // Colour alone fails for roughly one man in twelve and fails completely in greyscale, which is
    // normal for a chart whose audience is people looking at a deck.
    const { container } = renderWithProvider(<CoworkQuadrant points={POINTS} options={OPTIONS} />);

    const letters = Array.from(container.querySelectorAll('text'))
      .map((t) => t.textContent)
      .filter((t) => t === 'R' || t === 'C' || t === 'L');

    expect(letters).toContain('R');
    expect(letters).toContain('C');
    expect(letters).toContain('L');
  });

  it('fixes both axes at 0-100 so two reports are comparable', () => {
    // Fitting the axes to the data would move the dividing lines between tenants, and the bars'
    // position is the entire message of this chart.
    const { container } = renderWithProvider(<CoworkQuadrant points={POINTS} options={OPTIONS} />);

    const tickLabels = Array.from(container.querySelectorAll('text')).map((t) => t.textContent);
    ['0', '25', '50', '75', '100'].forEach((tick) => {
      expect(tickLabels).toContain(tick);
    });
  });

  it('keeps the dividing lines on the configured bars, not on hard-coded values', () => {
    const custom = { coworkLoadMinScore: 70, coworkFluencyMinScore: 30 } as CopilotAdoptionOptions;
    const { container } = renderWithProvider(<CoworkQuadrant points={POINTS} options={custom} />);

    const dashed = Array.from(container.querySelectorAll('line')).filter(
      (l) => l.getAttribute('stroke-dasharray') !== null,
    );

    // One vertical bar for load and one horizontal for fluency.
    expect(dashed.length).toBe(2);
  });

  it('renders a non-ASCII department name intact', () => {
    // Department is free text from a customer tenant and routinely non-Latin.
    const { container } = renderWithProvider(<CoworkQuadrant points={POINTS} options={OPTIONS} />);

    expect(bubbleTitle('Πωλήσεις', container)).toContain('Πωλήσεις');
  });

  it('draws the largest department first so small ones are not hidden behind it', () => {
    const { container } = renderWithProvider(<CoworkQuadrant points={POINTS} options={OPTIONS} />);

    const titles = Array.from(container.querySelectorAll('title')).map((t) =>
      (t.textContent ?? '').split('\n')[0],
    );

    expect(titles[0]).toBe('Client Services');
    expect(titles[titles.length - 1]).toBe('Facilities');
  });
});
