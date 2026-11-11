import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';

import { loadCatalog } from '../../i18n';
import { renderWithProvider } from '../../test/renderWithProvider';
import AdoptionPanel from './AdoptionPanel';
import OverviewPanel from './OverviewPanel';
import type { TeamsAdoption, TeamsOverview, TeamsWindow } from '../../types/teamsExplorer';

// What a reader without See PII is sent on the overview and adoption tabs (#844, S4-1): figures counted over the people the
// adoption breakdown is made of, and a figure about one to four people withheld. Withheld is drawn as a dash that says why,
// never as a zero, and the tab says, in the reader's language, that its figures leave out small groups.

const window28: TeamsWindow = {
  days: 28,
  fromUtc: '2026-02-21T00:00:00Z',
  toUtc: '2026-03-20T00:00:00Z',
  usageFromUtc: '2026-02-18T00:00:00Z',
  usageToUtc: '2026-03-17T00:00:00Z',
  workingDays: 20,
};

const noop = () => undefined;

function overview(over: Partial<TeamsOverview> = {}): TeamsOverview {
  return {
    window: window28,
    queries: [],
    kpis: {
      knownUsers: 120,
      activeUsers: 40,
      reachPct: 33.3,
      channelMessages: 1000,
      privateMessages: 600,
      openCollaborationPct: 62.5,
      meetingsAttended: 200,
      meetingsOrganised: 50,
      meetingsPerActiveUser: 5,
      audioHours: 80,
      videoSharePct: 40,
      screenShareSharePct: 10,
      calls: 90,
      totalTeams: 30,
      activeTeams: 12,
      totalChannels: 100,
      activeChannels: 40,
    },
    trend: [
      { weekStart: '2026-02-23T00:00:00Z', activeUsers: null, channelMessages: null, privateMessages: null, meetingsAttended: null, calls: null, withheld: true },
      { weekStart: '2026-03-02T00:00:00Z', activeUsers: 35, channelMessages: 400, privateMessages: 250, meetingsAttended: 80, calls: null },
      { weekStart: '2026-03-09T00:00:00Z', activeUsers: 38, channelMessages: 450, privateMessages: 280, meetingsAttended: 90, calls: null },
    ],
    segmentMix: [
      { segment: 'Power', label: 'Power', description: '', users: null, sharePct: null, withheld: true },
      { segment: 'Regular', label: 'Regular', description: '', users: 25, sharePct: null },
      { segment: 'Light', label: 'Light', description: '', users: null, sharePct: null, withheld: true },
      { segment: 'Dormant', label: 'Dormant', description: '', users: 78, sharePct: null },
    ],
    judgements: [{ key: 'team-sprawl', tone: 'good', headline: '12 of 30 teams saw a channel message (40%).', detail: '' }],
    dormancy: { known: false, reason: 'incompleteImport', expectedDays: 28, importedDays: 27, teamsLicensedUsers: 110, unmeasuredUsers: null },
    minimumGroupSize: 5,
    weeklyCallsWithheld: true,
    ...over,
  };
}

/** One to four people were active: every usage-report KPI is withheld, and the judgements worded from them are not sent. */
function fewActive(): TeamsOverview {
  const base = overview();
  return overview({
    usageWithheld: true,
    kpis: {
      ...base.kpis,
      knownUsers: null,
      activeUsers: null,
      reachPct: null,
      channelMessages: null,
      privateMessages: null,
      openCollaborationPct: null,
      meetingsAttended: null,
      meetingsOrganised: null,
      meetingsPerActiveUser: null,
      audioHours: null,
      videoSharePct: null,
      screenShareSharePct: null,
    },
    segmentMix: base.segmentMix.map((slice) => (slice.segment === 'Regular' ? { ...slice, users: null, withheld: true } : slice)),
  });
}

function adoption(over: Partial<TeamsAdoption> = {}): TeamsAdoption {
  return {
    window: window28,
    queries: [],
    groupBy: 'department',
    rhythm: { meanDailyActiveUsers: 18.5, weeklyActiveUsers: null, monthlyActiveUsers: 40, stickinessPct: 46.25 },
    segmentMix: [],
    segmentTrend: [
      { weekStart: '2026-02-23T00:00:00Z', power: null, regular: null, light: null, withheld: true },
      { weekStart: '2026-03-02T00:00:00Z', power: 10, regular: 15, light: 10 },
    ],
    breakdown: [
      { name: 'Contoso Analysts', knownUsers: 60, activeUsers: 25, reachPct: 41.7, messagesPerActiveUser: 30, meetingsPerActiveUser: 2 },
    ],
    devices: [
      { platform: 'Windows', users: 38, sharePct: null },
      { platform: 'Linux', users: null, sharePct: null, withheld: true },
    ],
    lifecycle: { newUsers: null, returningUsers: 30, lapsedUsers: null },
    minimumGroupSize: 5,
    otherGroups: 0,
    ...over,
  };
}

const WITHHELD_EN = 'Withheld: fewer than 5 people';
const WITHHELD_ES = 'Oculto: menos de 5 personas';
const READER_NOTE_EN =
  'To protect privacy, these figures count only people whose accounts are enabled in the directory and leave out groups of fewer than 5 people, so their totals can differ slightly from what someone with the See PII permission sees.';
const READER_NOTE_ES =
  'Para proteger la privacidad, estas cifras solo cuentan a las personas cuya cuenta está habilitada en el directorio y omiten los grupos de menos de 5 personas, así que sus totales pueden diferir ligeramente de los que ve alguien con el permiso Ver PII.';

describe('Teams Explorer overview for a reader without See PII', () => {
  it('says its figures leave out small groups, blanks the withheld weeks and segments, and never draws them as zero', () => {
    const { container } = renderWithProvider(<OverviewPanel data={overview()} />);

    expect(screen.getByText(READER_NOTE_EN)).toBeInTheDocument();
    expect(
      screen.getAllByText(
        'Weeks in which fewer than 5 people were active are left blank to protect privacy, together with the quietest other week, so that none can be worked out from the totals.',
      ),
    ).toHaveLength(2);
    expect(
      screen.getByText(
        /Segments of fewer than 5 people are hidden to protect privacy, together with the smallest other segment of active people/,
      ),
    ).toBeInTheDocument();
    expect(screen.getByText(/left out of this mix: fewer than 5\./)).toBeInTheDocument();
    // The donut's total would be the measured people less the withheld segments: a dash, not a misleading sum.
    expect(container.querySelector('svg[aria-label="measured users"]')?.textContent).toContain('\u2014');
    // The KPIs about forty people stand.
    expect(screen.getAllByText('33.3%').length).toBeGreaterThan(0);
    expect(screen.queryAllByRole('img', { name: WITHHELD_EN })).toHaveLength(0);
  });

  it('withholds every usage KPI as a dash that says why when one to four people were active, in English', () => {
    renderWithProvider(<OverviewPanel data={fewActive()} />);

    // Reach, open collaboration, meetings per active user and audio hours.
    expect(screen.getAllByRole('img', { name: WITHHELD_EN })).toHaveLength(4);
    expect(screen.getAllByText('Hidden to protect privacy')).toHaveLength(4);
    expect(screen.getByText('Fewer than 5 people used Teams in this period, so the figures about them are hidden to protect their privacy.')).toBeInTheDocument();
    expect(screen.queryByText('0%')).not.toBeInTheDocument();
    expect(screen.queryByText(/0 of 0/)).not.toBeInTheDocument();
    // Calls and teams are the Meetings and Collaboration tabs' figures, and stay.
    expect(screen.getAllByText('90').length).toBeGreaterThan(0);
  });

  it('says so in Spanish', async () => {
    await loadCatalog('es');
    renderWithProvider(<OverviewPanel data={fewActive()} />, { language: 'es' });

    expect(await screen.findByText(READER_NOTE_ES, undefined, { timeout: 5000 })).toBeInTheDocument();
    expect(screen.getAllByRole('img', { name: WITHHELD_ES })).toHaveLength(4);
    expect(screen.getAllByText('Oculto para proteger la privacidad')).toHaveLength(4);
    expect(
      screen.getByText('Menos de 5 personas usaron Teams en este periodo, así que las cifras sobre ellas se ocultan para proteger su privacidad.'),
    ).toBeInTheDocument();
    expect(
      screen.getAllByText(
        'Las semanas en las que estuvieron activas menos de 5 personas se dejan en blanco para proteger la privacidad, junto con la semana más tranquila de las demás, para que ninguna pueda deducirse de los totales.',
      ),
    ).toHaveLength(2);
    expect(screen.getByText(/que quedan fuera de esta combinación: menos de 5\./)).toBeInTheDocument();
    expect(screen.queryByText(READER_NOTE_EN)).not.toBeInTheDocument();
  });

  it('says nothing about small groups to a reader with See PII', () => {
    const seePii = overview({
      minimumGroupSize: null,
      weeklyCallsWithheld: undefined,
      trend: overview().trend.map((p, i) => ({ ...p, activeUsers: 30 + i, channelMessages: 300, privateMessages: 200, meetingsAttended: 60, calls: 20, withheld: undefined })),
      segmentMix: overview().segmentMix.map((slice) => ({ ...slice, users: slice.users ?? 3, sharePct: 10, withheld: undefined })),
      dormancy: { known: false, reason: 'incompleteImport', expectedDays: 28, importedDays: 27, teamsLicensedUsers: 110, unmeasuredUsers: 2 },
    });
    renderWithProvider(<OverviewPanel data={seePii} />);

    expect(screen.queryByText(READER_NOTE_EN)).not.toBeInTheDocument();
    expect(screen.queryByText(/left blank to protect privacy/)).not.toBeInTheDocument();
    expect(screen.queryByText(/are hidden to protect privacy/)).not.toBeInTheDocument();
    expect(screen.getByText(/left out of this mix: 2\./)).toBeInTheDocument();
    expect(screen.queryAllByRole('img', { name: WITHHELD_EN })).toHaveLength(0);
  });
});

describe('Teams Explorer adoption for a reader without See PII', () => {
  it('draws withheld rhythm and lifecycle figures as dashes, and says which weeks and platforms are left out', () => {
    renderWithProvider(<AdoptionPanel data={adoption()} groupBy="department" onGroupByChange={noop} demographicsAvailable />);

    expect(screen.getByText(READER_NOTE_EN)).toBeInTheDocument();
    // Weekly active users, newly active and lapsed.
    expect(screen.getAllByRole('img', { name: WITHHELD_EN })).toHaveLength(3);
    expect(screen.getAllByText('40').length).toBeGreaterThan(0);
    expect(screen.getByText(/Weeks in which a segment has fewer than 5 people are left blank to protect privacy\./)).toBeInTheDocument();
    expect(
      screen.getByText(/Platforms used by fewer than 5 people are not shown to protect privacy, and neither is the mobile total/),
    ).toBeInTheDocument();
    expect(screen.queryByText('Linux')).not.toBeInTheDocument();
  });

  it('says so in Spanish', async () => {
    await loadCatalog('es');
    renderWithProvider(<AdoptionPanel data={adoption()} groupBy="department" onGroupByChange={noop} demographicsAvailable />, {
      language: 'es',
    });

    expect(await screen.findByText(READER_NOTE_ES, undefined, { timeout: 5000 })).toBeInTheDocument();
    expect(screen.getAllByRole('img', { name: WITHHELD_ES })).toHaveLength(3);
    expect(
      screen.getByText(/Las semanas en las que un segmento tiene menos de 5 personas se dejan en blanco para proteger la privacidad\./),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/Las plataformas que usan menos de 5 personas no se muestran para proteger la privacidad/),
    ).toBeInTheDocument();
  });

  it('says nothing about small groups to a reader with See PII', () => {
    renderWithProvider(
      <AdoptionPanel
        data={adoption({
          minimumGroupSize: null,
          rhythm: { meanDailyActiveUsers: 18.5, weeklyActiveUsers: 4, monthlyActiveUsers: 41, stickinessPct: 45.1 },
          lifecycle: { newUsers: 3, returningUsers: 30, lapsedUsers: 8 },
          segmentTrend: [{ weekStart: '2026-03-02T00:00:00Z', power: 10, regular: 2, light: 10 }],
          devices: [
            { platform: 'Windows', users: 38, sharePct: 95 },
            { platform: 'Linux', users: 2, sharePct: 5 },
          ],
        })}
        groupBy="department"
        onGroupByChange={noop}
        demographicsAvailable
      />,
    );

    expect(screen.queryByText(READER_NOTE_EN)).not.toBeInTheDocument();
    expect(screen.queryAllByRole('img', { name: WITHHELD_EN })).toHaveLength(0);
    expect(screen.queryByText(/left blank to protect privacy/)).not.toBeInTheDocument();
    expect(screen.queryByText(/not shown to protect privacy/)).not.toBeInTheDocument();
  });
});
