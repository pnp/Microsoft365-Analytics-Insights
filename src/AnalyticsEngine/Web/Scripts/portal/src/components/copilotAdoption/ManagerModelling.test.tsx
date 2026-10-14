import { describe, expect, it } from 'vitest';
import { screen, within } from '@testing-library/react';
import { loadCatalog } from '../../i18n';
import { renderWithProvider } from '../../test/renderWithProvider';
import { ManagerModellingLine } from './ManagerModelling';
import { SegmentTable } from './adoptionShared';
import type {
  AdoptionSegmentRow,
  CopilotAdoptionSummary,
  ManagerModellingFigures,
  ManagerModellingSegmentRow,
} from '../../types/copilotAdoption';

/**
 * Do people managers use Copilot themselves (#641)? The one line on the executive view and the
 * department columns, in both languages. Synthetic figures only.
 */

const GREEK_DEPARTMENT = 'Καλημέρα κόσμε';

function figures(overrides: Partial<ManagerModellingFigures> = {}): ManagerModellingFigures {
  return {
    reportsWithManager: 120,
    managersStatusKnown: 40,
    managersStatusUnknown: 2,
    managersActive: 25,
    managersActivePct: 62.5,
    reportsManagerActive: 70,
    reportsActiveRatePctManagerActive: 71,
    reportsHabitRatePctManagerActive: 34,
    reportsManagerInactive: 44,
    reportsActiveRatePctManagerInactive: 48,
    reportsHabitRatePctManagerInactive: 19,
    reportsManagerUnknown: 6,
    reportsManagerActiveLicensed: 50,
    reportsActiveRatePctManagerActiveLicensed: 76,
    reportsHabitRatePctManagerActiveLicensed: 40,
    reportsManagerActiveUnlicensed: 20,
    reportsActiveRatePctManagerActiveUnlicensed: 58.5,
    reportsHabitRatePctManagerActiveUnlicensed: 20,
    reportsManagerInactiveLicensed: 30,
    reportsActiveRatePctManagerInactiveLicensed: 52,
    reportsHabitRatePctManagerInactiveLicensed: 21,
    reportsManagerInactiveUnlicensed: 14,
    reportsActiveRatePctManagerInactiveUnlicensed: 40,
    reportsHabitRatePctManagerInactiveUnlicensed: 14,
    ...overrides,
  };
}

/** Only the parts of a summary the line reads. */
function summary(overrides: Partial<ManagerModellingFigures> | null = {}): CopilotAdoptionSummary {
  return {
    options: { minSeatsPerSegment: 5, establishedScore: 50 },
    ...(overrides === null ? {} : figures(overrides)),
  } as unknown as CopilotAdoptionSummary;
}

const department = (name: string): AdoptionSegmentRow => ({
  segment: name,
  licensedUsers: 30,
  activeUsers: 18,
  habitualUsers: 8,
  neverUsedUsers: 6,
  adoptionRatePct: 60,
  averageAdoptionScore: 41,
});

const managerRow = (name: string, overrides: Partial<ManagerModellingFigures> = {}): ManagerModellingSegmentRow => ({
  segment: name,
  licensedUsers: 30,
  ...figures(overrides),
});

describe('ManagerModellingLine', () => {
  it('states the managers figure, the team comparison and the caveat in English', () => {
    renderWithProvider(<ManagerModellingLine summary={summary()} />);

    const line = screen.getByTestId('manager-modelling-line');
    expect(within(line).getByText('Do managers use Copilot themselves?')).toBeInTheDocument();
    expect(within(line).getByText(
      /^62\.5% of the 40 people managers whose own use is known used Copilot themselves in this period\. Their direct reports were active at 71% \(habit 34%\), against 48% \(habit 19%\) where the manager did not use it\./,
    )).toBeInTheDocument();
    expect(within(line).getByText(/2 more people managers' own use could not be determined, and is left out rather than counted as not using Copilot\./))
      .toBeInTheDocument();
    expect(within(line).getByText(
      'An association, not a cause: teams whose manager uses Copilot may differ in function, seniority or seat coverage.',
    )).toBeInTheDocument();
    expect(within(line).getByRole('button', { name: /Do managers use Copilot themselves\?/ })).toBeInTheDocument();
  });

  it('says the same in Spanish, with Spanish number formatting', async () => {
    await loadCatalog('es');
    renderWithProvider(<ManagerModellingLine summary={summary()} />, { language: 'es' });

    const line = screen.getByTestId('manager-modelling-line');
    expect(within(line).getByText('¿Usan Copilot los propios responsables?')).toBeInTheDocument();
    expect(within(line).getByText(
      /^El 62,5% de los 40 responsables de equipo cuyo uso propio se conoce usó Copilot en este periodo\. Las personas a su cargo directo estuvieron activas en un 71% \(hábito: 34%\), frente a un 48% \(hábito: 19%\) cuando el responsable no lo usó\./,
    )).toBeInTheDocument();
    expect(within(line).getByText(/No se ha podido determinar el uso propio de 2 responsables más/)).toBeInTheDocument();
    expect(within(line).getByText(
      'Es una asociación, no una causa: los equipos cuyo responsable usa Copilot pueden diferir en función, nivel jerárquico o cobertura de licencias.',
    )).toBeInTheDocument();
    expect(within(line).queryByText(/people managers/)).not.toBeInTheDocument();
  });

  it('uses the singular for one unknown manager', () => {
    renderWithProvider(<ManagerModellingLine summary={summary({ managersStatusUnknown: 1 })} />);

    expect(screen.getByText(/1 more people manager's own use could not be determined/)).toBeInTheDocument();
  });

  it('explains a withheld figure instead of showing it as zero', async () => {
    const withheld = summary({
      managersStatusKnown: 3,
      managersActive: null,
      managersActivePct: null,
      reportsActiveRatePctManagerActive: null,
      reportsActiveRatePctManagerInactive: null,
      reportsHabitRatePctManagerActive: null,
      reportsHabitRatePctManagerInactive: null,
      managersStatusUnknown: 0,
    });
    const { unmount } = renderWithProvider(<ManagerModellingLine summary={withheld} />);

    expect(screen.getByText(
      'Shown once the own use of at least 5 people managers is known, so that no one manager can be singled out. So far it is known for 3.',
    )).toBeInTheDocument();
    expect(screen.queryByText(/0%/)).not.toBeInTheDocument();
    unmount();

    await loadCatalog('es');
    renderWithProvider(<ManagerModellingLine summary={withheld} />, { language: 'es' });
    expect(screen.getByText(/Se muestra cuando se conoce el uso propio de al menos 5 responsables de equipo/)).toBeInTheDocument();
  });

  it('says why there is no comparison when one side has too few reports', () => {
    renderWithProvider(<ManagerModellingLine summary={summary({ reportsActiveRatePctManagerInactive: null, reportsHabitRatePctManagerInactive: null })} />);

    expect(screen.getByText(/There are too few direct reports on one side to compare the teams: each side needs at least 5\./)).toBeInTheDocument();
  });

  it('says when no licensed user has a recorded manager', () => {
    renderWithProvider(<ManagerModellingLine summary={summary({ reportsWithManager: 0, managersStatusKnown: 0, managersStatusUnknown: 0 })} />);

    expect(screen.getByText(
      'No licensed user analysed has a recorded manager, so whether managers use Copilot themselves cannot be shown.',
    )).toBeInTheDocument();
  });

  it('adds the split by seat in the analyst view, one complete pair at a time', () => {
    renderWithProvider(
      <ManagerModellingLine summary={summary({ reportsActiveRatePctManagerInactiveUnlicensed: null })} detail />,
    );

    expect(screen.getByText(/Managers with a Copilot seat: their direct reports were active at 76% where the manager used it, against 52% where they did not\./))
      .toBeInTheDocument();
    expect(screen.queryByText(/Managers without a seat/)).not.toBeInTheDocument();
  });

  it('renders nothing for a server that predates the figures', () => {
    renderWithProvider(<ManagerModellingLine summary={summary(null)} />);

    expect(screen.queryByTestId('manager-modelling-line')).not.toBeInTheDocument();
  });
});

describe('SegmentTable manager columns', () => {
  it('adds the manager columns beside each department in English, a dash for a withheld figure', () => {
    renderWithProvider(
      <SegmentTable
        rows={[department(GREEK_DEPARTMENT), department('Contoso Legal')]}
        segmentLabel="Department"
        managerModelling={[
          managerRow(GREEK_DEPARTMENT),
          managerRow('Contoso Legal', {
            managersStatusKnown: 2,
            managersStatusUnknown: 0,
            managersActivePct: null,
            reportsActiveRatePctManagerActive: null,
            reportsActiveRatePctManagerInactive: null,
            reportsHabitRatePctManagerActive: null,
            reportsHabitRatePctManagerInactive: null,
          }),
        ]}
      />,
    );

    for (const header of [
      'Managers using Copilot',
      'Active: manager uses Copilot',
      'Active: manager does not',
      'Habit: manager uses Copilot',
      'Habit: manager does not',
    ]) {
      expect(screen.getByRole('columnheader', { name: header })).toBeInTheDocument();
    }

    const greek = screen.getByText(GREEK_DEPARTMENT).closest('tr') as HTMLElement;
    expect(within(greek).getByText('62.5%')).toBeInTheDocument();
    expect(within(greek).getByText('Unknown: 2')).toBeInTheDocument();
    expect(within(greek).getByText('71%')).toBeInTheDocument();
    expect(within(greek).getByText('48%')).toBeInTheDocument();
    expect(within(greek).getByText('34%')).toBeInTheDocument();
    expect(within(greek).getByText('19%')).toBeInTheDocument();

    const legal = screen.getByText('Contoso Legal').closest('tr') as HTMLElement;
    const dashes = within(legal).getAllByLabelText('Too few people to show without singling someone out');
    expect(dashes).toHaveLength(5);
    expect(within(legal).queryByText('0%')).not.toBeInTheDocument();
  });

  it('translates the columns into Spanish and leaves the department names as stored', async () => {
    await loadCatalog('es');
    renderWithProvider(
      <SegmentTable
        rows={[department(GREEK_DEPARTMENT), department('(no department)')]}
        segmentLabel="Departamento"
        managerModelling={[managerRow(GREEK_DEPARTMENT), managerRow('(no department)')]}
      />,
      { language: 'es' },
    );

    for (const header of [
      'Responsables que usan Copilot',
      'Activos: el responsable usa Copilot',
      'Activos: el responsable no lo usa',
      'Hábito: el responsable usa Copilot',
      'Hábito: el responsable no lo usa',
    ]) {
      expect(screen.getByRole('columnheader', { name: header })).toBeInTheDocument();
    }

    const greek = screen.getByText(GREEK_DEPARTMENT).closest('tr') as HTMLElement;
    expect(within(greek).getByText('62,5%')).toBeInTheDocument();
    expect(within(greek).getByText('Sin determinar: 2')).toBeInTheDocument();
    expect(screen.getByText('(sin departamento)')).toBeInTheDocument();
    expect(screen.queryByText('Managers using Copilot')).not.toBeInTheDocument();
  });

  it('leaves a table without manager figures - the country table - as it was', () => {
    renderWithProvider(<SegmentTable rows={[department('Ruritania')]} segmentLabel="Country" />);

    expect(screen.queryByRole('columnheader', { name: 'Managers using Copilot' })).not.toBeInTheDocument();
    expect(screen.getAllByRole('columnheader')).toHaveLength(7);
  });
});
