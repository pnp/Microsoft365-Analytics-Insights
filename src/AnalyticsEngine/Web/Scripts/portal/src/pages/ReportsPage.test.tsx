import { describe, it, expect, beforeEach, vi } from 'vitest';
import { screen, fireEvent, waitFor } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import ReportsPage, { reportCategories, reportChartWarningText, reportMatrix, reportSeries } from './ReportsPage';
import { fetchReportAreas, fetchReportArea } from '../api/reportsApi';
import { fetchAvailability } from '../api/licenceActivityApi';
import { loadCatalog, translateStatic } from '../i18n';
import type { ReportAreaData, ReportAreas, ReportChart } from '../types/reports';

vi.mock('../api/reportsApi', () => ({ fetchReportAreas: vi.fn(), fetchReportArea: vi.fn() }));
vi.mock('../api/licenceActivityApi', () => ({ fetchAvailability: vi.fn() }));

const mockAreas = vi.mocked(fetchReportAreas);
const mockArea = vi.mocked(fetchReportArea);
const mockAvailability = vi.mocked(fetchAvailability);

const NO_AREAS: ReportAreas = {
  copilot: false,
  usage: false,
  spoAudit: false,
  webTraffic: false,
  calls: false,
  emails: false,
  officeApps: false,
};

const areaData: ReportAreaData = {
  area: 'copilot',
  months: 3,
  fromWeek: '2026-01-05T00:00:00Z',
  charts: [],
  cognitiveConfigured: true,
};

const baseChart = (overrides: Partial<ReportChart>): ReportChart => ({
  key: 'test',
  title: 'Test',
  description: 'Test chart',
  type: 'bar',
  valueLabel: 'People',
  series: null,
  categories: null,
  matrix: null,
  showShare: false,
  valueSuffix: null,
  sql: 'SELECT 1',
  error: null,
  errorKey: null,
  warning: null,
  seriesWarnings: null,
  ...overrides,
});

beforeEach(() => {
  vi.clearAllMocks();
  mockArea.mockResolvedValue(areaData);
});

describe('ReportsPage', () => {
  it('keeps the report loading state without mounting Licence activity while areas are pending', () => {
    mockAreas.mockReturnValue(new Promise(() => {}));
    renderWithProvider(<ReportsPage />);

    expect(screen.getByText('Loading reports...')).toBeVisible();
    expect(screen.queryByRole('tablist')).not.toBeInTheDocument();
    expect(screen.queryByText(/No built-in report charts are available yet/)).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Licence activity' })).toHaveAttribute('href', '#/insights/licence-activity');
    expect(mockAvailability).not.toHaveBeenCalled();
    expect(mockArea).not.toHaveBeenCalled();
  });

  it('shows the empty report state and a link to standalone Licence activity when imports are disabled', async () => {
    mockAreas.mockResolvedValue(NO_AREAS);
    renderWithProvider(<ReportsPage />);

    expect(await screen.findByText(/No built-in report charts are available yet/)).toBeInTheDocument();
    expect(screen.queryByRole('tablist')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Reporting period')).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Licence activity' })).toHaveAttribute('href', '#/insights/licence-activity');
    expect(mockAvailability).not.toHaveBeenCalled();
    expect(mockArea).not.toHaveBeenCalled();
  });

  it('keeps report tabs and their period control without embedding Licence activity', async () => {
    mockAreas.mockResolvedValue({ ...NO_AREAS, copilot: true });
    renderWithProvider(<ReportsPage />);

    expect(await screen.findByRole('tab', { name: 'Copilot' })).toBeInTheDocument();
    expect(screen.getByLabelText('Reporting period')).toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Licence activity' })).not.toBeInTheDocument();
    await waitFor(() => expect(mockArea).toHaveBeenCalledWith('copilot', 3, undefined));

    fireEvent.click(screen.getByRole('tab', { name: 'Copilot agents' }));
    await waitFor(() => expect(mockArea).toHaveBeenCalledWith('copilot-agents', 3, { topAgents: 8, agentName: '' }));
    fireEvent.change(screen.getByLabelText('Reporting period'), { target: { value: '6' } });
    await waitFor(() => expect(mockArea).toHaveBeenCalledWith('copilot-agents', 6, { topAgents: 8, agentName: '' }));
    expect(mockAvailability).not.toHaveBeenCalled();
  });

  it('surfaces report-areas errors without falling back to a Licence activity panel', async () => {
    mockAreas.mockRejectedValue(new Error('Boom: report areas failed to load (500).'));
    renderWithProvider(<ReportsPage />);

    expect(await screen.findByText(/report areas failed to load/i)).toBeInTheDocument();
    expect(screen.queryByRole('tablist')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Reporting period')).not.toBeInTheDocument();
    expect(screen.queryByText(/No built-in report charts are available yet/)).not.toBeInTheDocument();
    expect(mockAvailability).not.toHaveBeenCalled();
    expect(mockArea).not.toHaveBeenCalled();
  });

  /**
   * The Office apps area rides on the same Graph usage-report import as "Microsoft 365 usage", so a
   * deployment that imports usage reports gets both tabs and one that does not gets neither.
   */
  it('offers the Office apps tab only when the usage-report import is on', async () => {
    mockAreas.mockResolvedValue({ ...NO_AREAS, officeApps: true });
    renderWithProvider(<ReportsPage />);

    expect(await screen.findByRole('tab', { name: 'Office apps' })).toBeInTheDocument();
    await waitFor(() => expect(mockArea).toHaveBeenCalledWith('office-apps', 3, undefined));
  });

  it('hides the Office apps tab when that import is off', async () => {
    mockAreas.mockResolvedValue({ ...NO_AREAS, copilot: true });
    renderWithProvider(<ReportsPage />);

    expect(await screen.findByRole('tab', { name: 'Copilot' })).toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Office apps' })).not.toBeInTheDocument();
  });

  /**
   * A matrix chart must reach MatrixChart rather than falling through to "No data for this period."
   * - the fall-through branch is what every unrecognised chart type hits, so a missing case in the
   * renderer looks exactly like an empty result set.
   */
  it('renders a matrix chart returned by the Office apps area', async () => {
    mockAreas.mockResolvedValue({ ...NO_AREAS, officeApps: true });
    mockArea.mockResolvedValue({
      ...areaData,
      area: 'office-apps',
      charts: [
        {
          key: 'office-apps-by-department',
          title: 'App use by department',
          description: 'People using each app.',
          type: 'matrix',
          valueLabel: 'People',
          series: null,
          categories: null,
          matrix: {
            rowLabel: 'App',
            columnLabel: 'Department',
            rows: ['Excel'],
            columns: ['Finance'],
            cells: [{ row: 'Excel', column: 'Finance', value: 12 }],
            shadeByRow: true,
          },
          showShare: false,
          valueSuffix: null,
          sql: 'SELECT 1',
          error: null,
          warning: null,
        },
      ],
    });

    renderWithProvider(<ReportsPage />);

    expect(await screen.findByText('App use by department')).toBeInTheDocument();
    expect(screen.getByTitle('Excel / Finance: 12 People')).toBeInTheDocument();
    expect(screen.queryByText('No data for this period.')).not.toBeInTheDocument();
  });

  /** A percentage chart must print its unit, or 40 reads as forty people rather than 40%. */
  it('appends the unit suffix on a percentage bar chart', async () => {
    mockAreas.mockResolvedValue({ ...NO_AREAS, officeApps: true });
    mockArea.mockResolvedValue({
      ...areaData,
      area: 'office-apps',
      charts: [
        {
          key: 'office-apps-department-adoption',
          title: 'Departments least likely to use the apps',
          description: 'Share of each department.',
          type: 'bar',
          valueLabel: 'Adoption',
          series: null,
          categories: [{ label: 'Field Operations', value: 40 }],
          matrix: null,
          showShare: false,
          valueSuffix: '%',
          sql: 'SELECT 1',
          error: null,
          warning: null,
        },
      ],
    });

    renderWithProvider(<ReportsPage />);

    expect(await screen.findByText('Departments least likely to use the apps')).toBeInTheDocument();
    expect(screen.getByTitle('Field Operations: 40% Adoption')).toBeInTheDocument();
  });

  it('renders Office app-breadth labels through plural catalogue keys', async () => {
    mockAreas.mockResolvedValue({ ...NO_AREAS, officeApps: true });
    mockArea.mockResolvedValue({
      ...areaData,
      area: 'office-apps',
      charts: [
        {
          key: 'office-apps-breadth',
          title: 'How much of the suite people use',
          description: 'How many different Office apps each person used in the period.',
          type: 'bar',
          valueLabel: 'People',
          series: null,
          categories: [
            { label: '1 app', value: 5 },
            { label: '2 apps', value: 8 },
          ],
          matrix: null,
          showShare: true,
          valueSuffix: null,
          sql: 'SELECT 1',
          error: null,
          warning: null,
        },
      ],
    });

    renderWithProvider(<ReportsPage />);

    expect(await screen.findByText('How much of the suite people use')).toBeInTheDocument();
    expect(screen.getByText('1 app')).toBeInTheDocument();
    expect(screen.getByText('2 apps')).toBeInTheDocument();
  });

  it('translates Office platform categories while leaving product platform names untouched', async () => {
    await loadCatalog('es');
    const es = (key: Parameters<typeof translateStatic>[1], values?: Parameters<typeof translateStatic>[2]) =>
      translateStatic('es', key, values);

    const categories = reportCategories(es, baseChart({
      key: 'office-apps-platform-mix',
      categories: [
        { label: 'Windows', value: 10 },
        { label: 'Mac', value: 9 },
        { label: 'Mobile', value: 8 },
        { label: 'Web', value: 7 },
      ],
    }));
    expect(categories?.map((c) => c.label)).toEqual(['Windows', 'Mac', 'Móvil', 'Web']);

    const series = reportSeries(es, baseChart({
      key: 'office-apps-platform-trend',
      type: 'timeseries',
      series: [
        { name: 'Mobile', points: [] },
        { name: 'Web', points: [] },
      ],
    }));
    expect(series?.map((s) => s.name)).toEqual(['Móvil', 'Web']);

    const matrix = reportMatrix(es, baseChart({
      key: 'office-apps-platform-matrix',
      type: 'matrix',
      matrix: {
        rowLabel: 'App',
        columnLabel: 'Platform',
        rows: ['Word'],
        columns: ['Mobile', 'Web'],
        cells: [
          { row: 'Word', column: 'Mobile', value: 3 },
          { row: 'Word', column: 'Web', value: 4 },
        ],
        shadeByRow: true,
      },
    }));
    expect(matrix?.columns).toEqual(['Móvil', 'Web']);
    expect(matrix?.cells.map((cell) => cell.column)).toEqual(['Móvil', 'Web']);
  });

  it('translates structured usage-series warnings without translating workload or exception data', async () => {
    await loadCatalog('es');
    const es = (key: Parameters<typeof translateStatic>[1], values?: Parameters<typeof translateStatic>[2]) =>
      translateStatic('es', key, values);

    const warning = reportChartWarningText(es, baseChart({
      key: 'usage-active-users',
      warning: 'Some workload series are unavailable: Outlook: database failed; Teams: no settled usage data for the week of 2026-06-15.',
      seriesWarnings: [
        { series: 'Outlook', reason: 'loadFailed', error: 'database failed', week: null },
        { series: 'Teams', reason: 'noSettledDataForWeek', error: null, week: '2026-06-15T00:00:00Z' },
      ],
    }));

    expect(warning).toContain('Algunas series de cargas de trabajo no están disponibles: Outlook: database failed; Teams: no hay datos de uso consolidados para la semana del');
    expect(warning).not.toContain('Some workload series are unavailable');
    expect(warning).not.toContain('2026-06-15');
  });

  /**
   * A chart that explains why it is empty must not also print the generic "No data for this period."
   * The two together read as a contradiction, and the generic line is the less true of the pair -
   * "the Copilot import is switched off" and "no data in this period" are different facts.
   */
  it('shows only the explanation when a chart is empty for a stated reason', async () => {
    mockAreas.mockResolvedValue({ ...NO_AREAS, officeApps: true });
    mockArea.mockResolvedValue({
      ...areaData,
      area: 'office-apps',
      charts: [
        {
          key: 'office-apps-copilot-attach',
          title: 'Copilot take-up inside each app',
          description: 'Share who used Copilot in that app.',
          type: 'bar',
          valueLabel: 'Take-up',
          series: null,
          categories: [],
          matrix: null,
          showShare: false,
          valueSuffix: '%',
          sql: 'SELECT 1',
          error: null,
          warning: 'The Copilot usage report import is switched off.',
        },
      ],
    });

    renderWithProvider(<ReportsPage />);

    expect(await screen.findByText('The Copilot usage report import is switched off.')).toBeInTheDocument();
    expect(screen.queryByText('No data for this period.')).not.toBeInTheDocument();
  });

  /** A warning alongside real data must still draw the data - e.g. one series of several failed. */
  it('still renders the chart when a warning accompanies real data', async () => {    mockAreas.mockResolvedValue({ ...NO_AREAS, officeApps: true });
    mockArea.mockResolvedValue({
      ...areaData,
      area: 'office-apps',
      charts: [
        {
          key: 'office-apps-popularity',
          title: 'Most used apps',
          description: 'People per app.',
          type: 'bar',
          valueLabel: 'People',
          series: null,
          categories: [{ label: 'Excel', value: 12 }],
          matrix: null,
          showShare: false,
          valueSuffix: null,
          sql: 'SELECT 1',
          error: null,
          warning: 'One series could not be loaded.',
        },
      ],
    });

    renderWithProvider(<ReportsPage />);

    expect(await screen.findByText('One series could not be loaded.')).toBeInTheDocument();
    expect(screen.getByTitle('Excel: 12 People')).toBeInTheDocument();
  });

  /**
   * The Office apps queries read one record per person per day, so the server caps their window
   * below the six months the period control offers. Silently charting a different period from the
   * one selected is worse than the shorter window itself.
   */
  it('says so when the server charted a shorter window than the one selected', async () => {
    mockAreas.mockResolvedValue({ ...NO_AREAS, officeApps: true });
    mockArea.mockResolvedValue({ ...areaData, area: 'office-apps', months: 3, charts: [] });

    renderWithProvider(<ReportsPage />);

    expect(await screen.findByRole('tab', { name: 'Office apps' })).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Reporting period'), { target: { value: '6' } });

    expect(await screen.findByText(/Showing the last 3 months rather than 6/)).toBeInTheDocument();
  });

  it('stays quiet when the server used the window that was asked for', async () => {
    mockAreas.mockResolvedValue({ ...NO_AREAS, officeApps: true });
    mockArea.mockResolvedValue({ ...areaData, area: 'office-apps', months: 3, charts: [] });

    renderWithProvider(<ReportsPage />);

    expect(await screen.findByRole('tab', { name: 'Office apps' })).toBeInTheDocument();
    await waitFor(() => expect(mockArea).toHaveBeenCalledWith('office-apps', 3, undefined));
    expect(screen.queryByText(/Showing the last/)).not.toBeInTheDocument();
  });
});
