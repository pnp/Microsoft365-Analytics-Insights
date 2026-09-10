import { describe, it, expect } from 'vitest';
import { screen, within } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import ResourceTypesPanel from './ResourceTypesPanel';
import { CopilotResourceTypeKind } from '../../types/copilotAdoption';
import type { AdoptionResourceTypeRow } from '../../types/copilotAdoption';

/**
 * The rows the server sends: the raw AccessedResources.Type values from Microsoft's audit log, each
 * with the kind the shared C# taxonomy assigned it.
 */
const ROWS: AdoptionResourceTypeRow[] = [
  { label: 'CITATION', value: 4200, kind: CopilotResourceTypeKind.UsageRole },
  { label: 'docx', value: 480, kind: CopilotResourceTypeKind.TenantContent },
  { label: 'WebSearchQuery', value: 300, kind: CopilotResourceTypeKind.ExternalGrounding },
  { label: 'xlsx', value: 210, kind: CopilotResourceTypeKind.TenantContent },
  { label: '(unknown)', value: 90, kind: CopilotResourceTypeKind.Unclassified },
];

/** The group headings, in the order the panel shows them. */
const GROUP_TITLES = [
  'Tenant content',
  'How it was used',
  'Grounding from outside the tenant',
  'Unclassified',
];

/** The rendered container for one group, or null when the group has no rows. */
function groupContainer(title: string): HTMLElement | null {
  const heading = screen.queryByText(title);
  // <Text> heading -> the heading block -> the group.
  return (heading?.parentElement?.parentElement as HTMLElement) ?? null;
}

/** The group heading a label is rendered under, or null if the label is not on screen at all. */
function groupOf(label: string): string | null {
  for (const title of GROUP_TITLES) {
    const group = groupContainer(title);
    if (group && within(group).queryByText(label)) return title;
  }
  return null;
}

describe('ResourceTypesPanel', () => {
  it('does not present CITATION as a kind of tenant content', () => {
    // The defect in issue #468: CITATION describes how the resource was used, not what it is, and it
    // is usually the largest bucket - so charting it as tenant content made the card's own subtitle
    // false for most of its ink.
    renderWithProvider(<ResourceTypesPanel rows={ROWS} />);

    const group = groupOf('CITATION');
    expect(group).toBe('How it was used');
    expect(group).not.toBe('Tenant content');
  });

  it('keeps web grounding out of the tenant content group', () => {
    renderWithProvider(<ResourceTypesPanel rows={ROWS} />);

    expect(groupOf('WebSearchQuery')).toBe('Grounding from outside the tenant');
  });

  it('groups file kinds as tenant content', () => {
    renderWithProvider(<ResourceTypesPanel rows={ROWS} />);

    expect(groupOf('docx')).toBe('Tenant content');
    expect(groupOf('xlsx')).toBe('Tenant content');
  });

  it('shows a reference with no type as unclassified rather than as content', () => {
    renderWithProvider(<ResourceTypesPanel rows={ROWS} />);

    expect(groupOf('(unknown)')).toBe('Unclassified');
  });

  it('shows a value Microsoft has not used yet as unclassified, and never drops it', () => {
    // Microsoft publishes no enumeration for this field, so an unrecognised value is expected rather
    // than exceptional. It must stay visible and visibly unclassified.
    const future: AdoptionResourceTypeRow[] = [
      ...ROWS,
      { label: 'SomeTypeMicrosoftAddedLater', value: 1500, kind: CopilotResourceTypeKind.Unclassified },
    ];
    renderWithProvider(<ResourceTypesPanel rows={future} />);

    expect(groupOf('SomeTypeMicrosoftAddedLater')).toBe('Unclassified');
  });

  it('keeps a row whose kind this build does not know about', () => {
    // A newer server adding an enum member must not make references disappear from the chart - that
    // would be a worse version of the silent misclassification this card was reported for.
    const unknownKind: AdoptionResourceTypeRow[] = [
      ...ROWS,
      { label: 'FromANewerServer', value: 75, kind: 99 as CopilotResourceTypeKind },
    ];
    renderWithProvider(<ResourceTypesPanel rows={unknownKind} />);

    expect(groupOf('FromANewerServer')).toBe('Unclassified');
  });

  it('scales bars against the largest value across all groups, not within a group', () => {
    // Grouping is what makes the different taxonomies readable, but it must not rescale each group to
    // its own maximum: docx at 480 would then be drawn the same width as CITATION at 4200.
    const { container } = renderWithProvider(<ResourceTypesPanel rows={ROWS} />);

    const widths = new Map<string, number>();
    ROWS.forEach((row) => {
      const cell = screen.getByText(row.label);
      const bar = cell.parentElement?.querySelector('div > div[style*="width"]') as HTMLElement | null;
      if (bar) widths.set(row.label, parseFloat(bar.style.width));
    });

    expect(container).toBeTruthy();
    expect(widths.get('CITATION')).toBeCloseTo(100, 5);
    expect(widths.get('docx')).toBeCloseTo((480 / 4200) * 100, 5);
  });

  it('renders an empty state rather than an empty chart', () => {
    renderWithProvider(<ResourceTypesPanel rows={[]} />);

    expect(screen.getByText('No data for this period.')).toBeTruthy();
  });

  it('does not describe the tenant content group without warning that it undercounts', () => {
    // A cited PDF is typed CITATION, so the file-kind rows are not "how many PDFs Copilot used".
    // The card is only honest if it says so.
    renderWithProvider(<ResourceTypesPanel rows={ROWS} />);

    const heading = screen.getByText('Tenant content');
    const group = heading.parentElement as HTMLElement;
    expect(within(group).getByText(/Undercounted/)).toBeTruthy();
  });
});
