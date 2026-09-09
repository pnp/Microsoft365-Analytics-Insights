import { describe, it, expect, vi } from 'vitest';
import { screen, fireEvent } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import SelectedLicenceBar from './SelectedLicenceBar';
import type { LicenceActivitySku } from '../../types/licenceActivity';

const licences: LicenceActivitySku[] = [
  { licenceTypeId: 20, name: 'F3', skuId: 'DESKLESSPACK', assignedUsers: 50, workloads: [] },
  { licenceTypeId: 10, name: 'E5', skuId: 'ENTERPRISEPREMIUM', assignedUsers: 100, workloads: [] },
];

describe('SelectedLicenceBar', () => {
  it('lists licences biggest-first and reflects the current selection', () => {
    renderWithProvider(<SelectedLicenceBar licences={licences} selectedLicenceTypeId={10} onSelect={vi.fn()} />);

    const select = screen.getByLabelText('Selected licence') as HTMLSelectElement;
    expect(select.value).toBe('10');
    // Biggest first: E5 (100) before F3 (50).
    const options = Array.from(select.querySelectorAll('option')).map((o) => o.textContent);
    expect(options).toEqual(['E5', 'F3']);

    // The selected licence's own assigned count is shown as context.
    expect(screen.getByText(/100 people hold this licence/)).toBeInTheDocument();
  });

  it('changes the licence via the dropdown', () => {
    const onSelect = vi.fn();
    renderWithProvider(<SelectedLicenceBar licences={licences} selectedLicenceTypeId={10} onSelect={onSelect} />);

    fireEvent.change(screen.getByLabelText('Selected licence'), { target: { value: '20' } });
    expect(onSelect).toHaveBeenCalledWith(20);
  });

  it('renders nothing when there are no licences', () => {
    renderWithProvider(<SelectedLicenceBar licences={[]} selectedLicenceTypeId={null} onSelect={vi.fn()} />);
    expect(screen.queryByLabelText('Selected licence')).not.toBeInTheDocument();
  });

  it('renders a non-Latin (Greek) licence name without corruption', () => {
    renderWithProvider(
      <SelectedLicenceBar
        licences={[{ licenceTypeId: 30, name: 'Άδεια Καλημέρα', skuId: 'GREEK', assignedUsers: 5, workloads: [] }]}
        selectedLicenceTypeId={30}
        onSelect={vi.fn()}
      />,
    );
    expect(screen.getByRole('option', { name: 'Άδεια Καλημέρα' })).toBeInTheDocument();
  });
});
