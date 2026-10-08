import { beforeAll, describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import LeadershipComparisonCard from './LeadershipComparisonCard';
import { loadCatalog } from '../../i18n';
import type { LeadershipAdoptionComparison } from '../../types/leadershipCohort';

const empty: LeadershipAdoptionComparison = {
  status: 'notConfigured',
  reason: null,
  minimumCohort: 10,
  licensedLeaders: null,
  activeLeaders: null,
  habitualLeaders: null,
  leaderAdoptionRatePct: null,
  leaderHabitRatePct: null,
  leaderAverageScore: null,
  tenantAdoptionRatePct: null,
  tenantHabitRatePct: null,
  tenantAverageScore: null,
  adoptionGapPts: null,
  habitGapPts: null,
  scoreGap: null,
  membershipRefreshedUtc: null,
  figuresIncomplete: false,
};

const ok: LeadershipAdoptionComparison = {
  ...empty,
  status: 'ok',
  licensedLeaders: 20,
  activeLeaders: 15,
  habitualLeaders: 8,
  leaderAdoptionRatePct: 75,
  leaderHabitRatePct: 40,
  leaderAverageScore: 61.5,
  tenantAdoptionRatePct: 60,
  tenantHabitRatePct: 45,
  tenantAverageScore: 50,
  adoptionGapPts: 15,
  habitGapPts: -5,
  scoreGap: 11.5,
  membershipRefreshedUtc: '2026-10-01T08:00:00Z',
};

beforeAll(async () => {
  await loadCatalog('es');
});

describe('LeadershipComparisonCard', () => {
  it('renders nothing for a summary from a server without the comparison', () => {
    renderWithProvider(<LeadershipComparisonCard comparison={undefined} />);
    expect(screen.queryByText('Leadership group compared with the tenant')).not.toBeInTheDocument();
  });

  it('shows leader and tenant figures side by side with signed gaps', () => {
    renderWithProvider(<LeadershipComparisonCard comparison={ok} />);
    expect(screen.getByText(/20 licensed leaders, 15 of them active/)).toBeInTheDocument();
    expect(screen.getByText('75%')).toBeInTheDocument();
    expect(screen.getByText('60%')).toBeInTheDocument();
    expect(screen.getByText('+15.0 pts')).toBeInTheDocument();
    expect(screen.getByText('\u22125.0 pts')).toBeInTheDocument();
    expect(screen.getByText('61.5')).toBeInTheDocument();
    expect(screen.getByText('+11.5')).toBeInTheDocument();
  });

  it('withholds every figure, including the count, when the cohort is too small', () => {
    renderWithProvider(<LeadershipComparisonCard comparison={{ ...empty, status: 'suppressed' }} />);
    expect(screen.getByText(/fewer than 10 members of the leadership group hold a Copilot licence/)).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('explains why the comparison is unavailable', () => {
    renderWithProvider(<LeadershipComparisonCard comparison={{ ...empty, status: 'unavailable', reason: 'permissionMissing' }} />);
    expect(screen.getByText('The comparison is unavailable: the application is missing permission to read group members.')).toBeInTheDocument();
  });

  it('hides stale membership rather than showing old figures', () => {
    renderWithProvider(<LeadershipComparisonCard comparison={{ ...empty, status: 'stale', membershipRefreshedUtc: '2026-09-01T08:00:00Z' }} />);
    expect(screen.getByText(/were last read successfully on/)).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('points a narrowed view at the whole tenant', () => {
    renderWithProvider(<LeadershipComparisonCard comparison={{ ...empty, status: 'scopedView' }} />);
    expect(screen.getByText(/only for the whole tenant/)).toBeInTheDocument();
  });

  it('renders in Spanish', () => {
    renderWithProvider(<LeadershipComparisonCard comparison={{ ...empty, status: 'suppressed' }} />, { language: 'es' });
    expect(screen.getByText(/menos de 10 miembros del grupo directivo/)).toBeInTheDocument();
  });
});
