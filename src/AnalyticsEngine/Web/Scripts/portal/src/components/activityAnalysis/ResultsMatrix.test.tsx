import { describe, expect, it } from 'vitest';
import { fireEvent, screen } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import ResultsMatrix, { DEPARTMENTS_PER_PAGE } from './ResultsMatrix';
import type { ActivityAnalysisMetric, ActivityAnalysisQuery, ActivityAnalysisReport } from '../../types/activityAnalysis';

const QUERY: ActivityAnalysisQuery = { from: '2026-09-07', to: '2026-09-28', metrics: ['teams.calls'] };

const METRICS = new Map<string, ActivityAnalysisMetric>([
  ['teams.calls', { key: 'teams.calls', category: 'teams', unit: 'count', core: true, available: true, label: 'Teams Calls' }],
]);

const department = (i: number) => `Department ${String(i).padStart(3, '0')}`;

/** A synthetic report of `count` departments, where department i has i people. */
function report(count: number): ActivityAnalysisReport {
  const departments = Array.from({ length: count }, (_, k) => ({
    name: department(k + 1),
    other: false,
    people: k + 1,
    values: [{ metric: 'teams.calls', sum: (k + 1) * 10, unique: k + 1 }],
  }));
  const people = departments.reduce((sum, d) => sum + d.people, 0);
  return {
    generatedUtc: '2026-10-06T10:00:00Z',
    from: QUERY.from,
    to: QUERY.to,
    weekStarts: ['2026-09-07'],
    metrics: QUERY.metrics,
    populationPeople: people,
    matchingPeople: people,
    activePeople: people,
    suppressed: false,
    series: [{ metric: 'teams.calls', sum: [people], activePeople: [people] }],
    byCompany: { rows: [], otherGroups: 0 },
    byDepartment: { rows: [], otherGroups: 0 },
    departments,
    otherDepartments: 0,
    total: { people, values: [{ metric: 'teams.calls', sum: people * 10, unique: people }] },
    licences: [],
    rangeMaxima: [],
    userFilter: null,
  };
}

describe('ResultsMatrix - a long department list', () => {
  it('draws a page of departments at a time, with the rest one click away', () => {
    renderWithProvider(<ResultsMatrix report={report(250)} query={QUERY} metrics={METRICS} />);

    expect(DEPARTMENTS_PER_PAGE).toBe(100);
    expect(screen.getByText(department(100))).toBeInTheDocument();
    expect(screen.queryByText(department(101))).not.toBeInTheDocument();
    expect(screen.getByText('Showing 100 of 250 rows.')).toBeInTheDocument();
    expect(screen.getByText('Total')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Show 100 more' }));
    expect(screen.getByText(department(200))).toBeInTheDocument();
    expect(screen.queryByText(department(201))).not.toBeInTheDocument();
    expect(screen.getByText('Showing 200 of 250 rows.')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Show 50 more' }));
    expect(screen.getByText(department(250))).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Show \d+ more$/ })).not.toBeInTheDocument();
    expect(screen.queryByText(/^Showing \d+ of \d+ rows\.$/)).not.toBeInTheDocument();
  });

  it('pages through the order the reader chose, and starts from one page again for a new report', () => {
    const { rerender } = renderWithProvider(<ResultsMatrix report={report(250)} query={QUERY} metrics={METRICS} />);

    // Figures sort largest first, so the first page is the 100 largest departments.
    fireEvent.click(screen.getByRole('button', { name: 'People' }));
    expect(screen.getByText(department(250))).toBeInTheDocument();
    expect(screen.getByText(department(151))).toBeInTheDocument();
    expect(screen.queryByText(department(150))).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Show 100 more' }));
    expect(screen.getByText(department(51))).toBeInTheDocument();

    rerender(<ResultsMatrix report={report(250)} query={QUERY} metrics={METRICS} />);
    expect(screen.getByText(department(151))).toBeInTheDocument();
    expect(screen.queryByText(department(150))).not.toBeInTheDocument();
  });

  it('draws a short list whole', () => {
    renderWithProvider(<ResultsMatrix report={report(DEPARTMENTS_PER_PAGE)} query={QUERY} metrics={METRICS} />);

    expect(screen.getByText(department(1))).toBeInTheDocument();
    expect(screen.getByText(department(DEPARTMENTS_PER_PAGE))).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Show \d+ more$/ })).not.toBeInTheDocument();
  });
});
