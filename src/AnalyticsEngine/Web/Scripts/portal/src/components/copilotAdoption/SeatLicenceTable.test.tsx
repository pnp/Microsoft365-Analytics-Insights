import { describe, it, expect } from 'vitest';
import { screen, within } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import SeatLicenceTable from './SeatLicenceTable';
import type { LicenceTypeClassification } from '../../types/copilotAdoption';

function seat(id: number, name: string, sku: string, assignedUsers: number, assignedIdleUsers: number | null): LicenceTypeClassification {
  return {
    id,
    name,
    skuPartNumber: sku,
    assignedUsers,
    purchasedUnits: null,
    unassignedUnits: null,
    assignedIdleUsers,
    purchasedUnitsRefreshedUtc: null,
    isCopilotSeat: true,
  };
}

/** What a reader without See PII is sent: a pilot of three holders, and the smallest seat that would give it back. */
const withheldForReader: LicenceTypeClassification[] = [
  seat(1, 'Microsoft 365 Copilot', 'Microsoft_365_Copilot', 1250, 1234),
  { ...seat(2, 'Contoso Copilot pilot', 'CONTOSO_COPILOT_PILOT', 3, null), assignedIdleWithheld: true, assignedIdleWithheldReason: 'fewHolders', minimumHolders: 5 },
  {
    ...seat(3, 'Microsoft 365 Copilot EDU', 'Microsoft_365_Copilot_EDU', 9, null),
    assignedIdleWithheld: true,
    assignedIdleWithheldReason: 'revealsSmallerSeat',
    minimumHolders: 5,
  },
];

function idleCell(sku: string): HTMLElement {
  const row = screen.getAllByRole('row').find((r) => within(r).queryByText(sku))!;
  return within(row).getAllByRole('cell')[5];
}

describe('SeatLicenceTable', () => {
  it('says why a few holders\u2019 idle seats are hidden from a reader without See PII, never showing 0', () => {
    renderWithProvider(<SeatLicenceTable licences={withheldForReader} />);

    // The whole cell is the reason: no 0, and no figure, beside it.
    expect(idleCell('CONTOSO_COPILOT_PILOT')).toHaveTextContent(/^Hidden: fewer than 5 people hold this licence$/);
    expect(idleCell('Microsoft_365_Copilot_EDU')).toHaveTextContent(
      /^Hidden: with the reclaimable total, it would reveal the figure for a licence fewer than 5 people hold$/,
    );
    expect(idleCell('Microsoft_365_Copilot')).toHaveTextContent(/^1,234$/);
    // How many hold the pilot stays, as Licence activity keeps it.
    const pilot = screen.getAllByRole('row').find((r) => within(r).queryByText('CONTOSO_COPILOT_PILOT'))!;
    expect(within(pilot).getByText('3')).toBeInTheDocument();
    expect(
      screen.getByText(
        'Idle seats are hidden for a Copilot licence that fewer than 5 people hold, so that nobody can be singled out. They are also hidden for one larger Copilot licence: subtracting its figure from the reclaimable total would otherwise reveal the hidden one.',
      ),
    ).toBeInTheDocument();
  });

  it('says the same in Spanish, with the number formatted for Spanish', async () => {
    renderWithProvider(<SeatLicenceTable licences={withheldForReader} />, { language: 'es' });

    // The Spanish catalog arrives after the first render.
    expect(await screen.findByRole('columnheader', { name: 'Asignadas inactivas' }, { timeout: 5000 })).toBeInTheDocument();
    expect(idleCell('CONTOSO_COPILOT_PILOT')).toHaveTextContent(/^Oculto: menos de 5 personas tienen esta licencia$/);
    expect(idleCell('Microsoft_365_Copilot_EDU')).toHaveTextContent(
      /^Oculto: junto con el total de licencias recuperables, revelaría la cifra de una licencia que tienen menos de 5 personas$/,
    );
    expect(idleCell('Microsoft_365_Copilot')).toHaveTextContent(/^1234$/);
    expect(screen.getByText(/^Los puestos sin uso se ocultan en una licencia de Copilot que tienen menos de 5 personas/)).toBeInTheDocument();
  });

  it('shows every idle count, and no note, when nothing is withheld - as a reader with See PII is sent', () => {
    renderWithProvider(
      <SeatLicenceTable
        licences={[seat(1, 'Microsoft 365 Copilot', 'Microsoft_365_Copilot', 40, 7), seat(2, 'Contoso Copilot pilot', 'CONTOSO_COPILOT_PILOT', 3, 2)]}
      />,
    );

    expect(idleCell('CONTOSO_COPILOT_PILOT')).toHaveTextContent(/^2$/);
    expect(idleCell('Microsoft_365_Copilot')).toHaveTextContent(/^7$/);
    expect(screen.queryByText(/Hidden/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Idle seats are hidden/)).not.toBeInTheDocument();
  });

  it('says only why a seat is hidden when no larger seat had to be hidden with it', () => {
    renderWithProvider(
      <SeatLicenceTable
        licences={[{ ...seat(2, 'Contoso Copilot pilot', 'CONTOSO_COPILOT_PILOT', 3, null), assignedIdleWithheld: true, assignedIdleWithheldReason: 'fewHolders', minimumHolders: 5 }]}
      />,
    );

    expect(screen.getByText('Idle seats are hidden for a Copilot licence that fewer than 5 people hold, so that nobody can be singled out.')).toBeInTheDocument();
    expect(screen.queryByText(/also hidden/)).not.toBeInTheDocument();
  });
});
