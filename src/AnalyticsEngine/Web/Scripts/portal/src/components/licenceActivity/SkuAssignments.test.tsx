import { describe, it, expect, vi } from 'vitest';
import { screen, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../../test/renderWithProvider';
import SkuAssignments from './SkuAssignments';
import type { LicenceActivitySku } from '../../types/licenceActivity';

const licences: LicenceActivitySku[] = [
  { licenceTypeId: 10, name: 'E5', skuId: 'ENTERPRISEPREMIUM', assignedUsers: 100, workloads: [] },
  { licenceTypeId: 20, name: 'F3', skuId: 'DESKLESSPACK', assignedUsers: 50, workloads: [] },
];

describe('SkuAssignments', () => {
  it('lists licences with assigned counts and selects one on click', () => {
    const onSelect = vi.fn();
    renderWithProvider(<SkuAssignments licences={licences} selectedLicenceTypeId={10} onSelect={onSelect} />);

    expect(screen.getByText('E5')).toBeInTheDocument();
    expect(screen.getByText('100')).toBeInTheDocument();

    fireEvent.click(screen.getByText('F3'));
    expect(onSelect).toHaveBeenCalledWith(20);
  });

  it('selects from anywhere in the row, and the button still fires exactly once', () => {
    // The row carries a pointer cursor and a hover highlight across its whole width, but activation
    // used to live only in the first cell's button - so the assigned-users cell and the distribution
    // cells advertised a click target that did nothing. Both directions are pinned here: a click on a
    // NON-button cell now selects (the affordance is honest), and a click on the button itself still
    // results in exactly ONE onSelect call rather than two via bubbling.
    const onSelect = vi.fn();
    renderWithProvider(<SkuAssignments licences={licences} selectedLicenceTypeId={null} onSelect={onSelect} />);

    // Direction 1: the previously-dead area of the row now activates.
    fireEvent.click(screen.getByText('50'));
    expect(onSelect).toHaveBeenCalledTimes(1);
    expect(onSelect).toHaveBeenCalledWith(20);

    // Direction 2: the real button is not double-counted by the new row handler.
    onSelect.mockClear();
    fireEvent.click(screen.getByRole('button', { name: /E5/ }));
    expect(onSelect).toHaveBeenCalledTimes(1);
    expect(onSelect).toHaveBeenCalledWith(10);
  });

  it('activates selection via a real button, keeping native table-row semantics', () => {
    renderWithProvider(<SkuAssignments licences={licences} selectedLicenceTypeId={10} onSelect={vi.fn()} />);

    // The rows keep native semantics - no role="button" hijacking the row/cell roles (which would
    // also fold every cell, including the five distribution bars, into one huge button label).
    const rows = screen.getAllByRole('row');
    expect(rows.length).toBe(3); // header + two SKU rows
    rows.forEach((r) => expect(r).not.toHaveAttribute('role'));

    // Selection is a real, focusable button per row; the selected one is pressed.
    const buttons = screen.getAllByRole('button');
    expect(buttons.length).toBe(2);
    const e5 = screen.getByRole('button', { name: /E5/ });
    expect(e5).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: /F3/ })).toHaveAttribute('aria-pressed', 'false');

    // The accessible name is concise (the SKU), not a 60-word row concatenation: the distribution
    // labels live in other cells and are not part of the control.
    expect(e5).not.toHaveTextContent('High');
  });

  it('selects with the keyboard via the real button', async () => {
    const user = userEvent.setup();
    const onSelect = vi.fn();
    renderWithProvider(<SkuAssignments licences={licences} selectedLicenceTypeId={null} onSelect={onSelect} />);

    const f3 = screen.getByRole('button', { name: /F3/ });
    f3.focus();
    await user.keyboard('{Enter}');
    expect(onSelect).toHaveBeenCalledWith(20);
  });

  it('renders a non-Latin (Greek) SKU name without corruption', () => {
    // A SKU name is a Unicode-safe field.
    renderWithProvider(
      <SkuAssignments
        licences={[{ licenceTypeId: 30, name: 'Άδεια Καλημέρα', skuId: 'GREEK_SKU', assignedUsers: 5, workloads: [] }]}
        selectedLicenceTypeId={null}
        onSelect={vi.fn()}
      />,
    );
    expect(screen.getByText('Άδεια Καλημέρα')).toBeInTheDocument();
  });

  it('scales to many SKUs: sorts biggest-first, filters, and stays selectable', () => {
    const many: LicenceActivitySku[] = Array.from({ length: 12 }, (_, i) => ({
      licenceTypeId: i + 1,
      name: `SKU ${i + 1}`,
      skuId: `PART${i + 1}`,
      assignedUsers: (i + 1) * 10,
      workloads: [],
    }));
    // A sparse licence with a non-Latin name (the kind that's easy to lose among 50 SKUs).
    many.push({ licenceTypeId: 99, name: 'Άδεια σπάνια', skuId: 'RARE', assignedUsers: 1, workloads: [] });

    const onSelect = vi.fn();
    renderWithProvider(<SkuAssignments licences={many} selectedLicenceTypeId={null} onSelect={onSelect} />);

    // Biggest first (assigned desc): SKU 12 (120) leads.
    expect(screen.getAllByRole('button')[0]).toHaveTextContent('SKU 12');

    // The filter narrows the (bounded, scrollable) list to the sparse one.
    fireEvent.change(screen.getByLabelText('Filter licences'), { target: { value: 'σπάνια' } });
    expect(screen.getByText('Άδεια σπάνια')).toBeInTheDocument();
    expect(screen.queryByText('SKU 12')).not.toBeInTheDocument();

    fireEvent.click(screen.getByText('Άδεια σπάνια'));
    expect(onSelect).toHaveBeenCalledWith(99);
  });

  it('filters and selects unnamed SKUs using a real identifier fallback', () => {
    const many: LicenceActivitySku[] = Array.from({ length: 9 }, (_, i) => ({
      licenceTypeId: i + 1, name: i < 2 ? null : `SKU ${i}`,
      skuId: i === 0 ? 'CONTOSO_UNKNOWN' : null,
      assignedUsers: i + 1, workloads: [],
    }));
    const onSelect = vi.fn();
    renderWithProvider(<SkuAssignments licences={many} selectedLicenceTypeId={null} onSelect={onSelect} />);
    fireEvent.change(screen.getByLabelText('Filter licences'), { target: { value: 'CONTOSO_UNKNOWN' } });
    const identifier = screen.getByRole('button', { name: /CONTOSO_UNKNOWN/ });
    fireEvent.click(identifier);
    expect(onSelect).toHaveBeenCalledWith(1);
    fireEvent.change(screen.getByLabelText('Filter licences'), { target: { value: 'Licence 2' } });
    expect(screen.getByRole('button', { name: 'Licence 2' })).toBeInTheDocument();
  });
});
