import { describe, it, expect, vi } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../../test/renderWithProvider';
import { RationaleCell, SortableTh } from './adoptionShared';

const INFO = {
  what: 'Distinct days this person had at least one Copilot interaction.',
  how: 'Counted from the audit log.',
};

function renderHeader(onSort = vi.fn()) {
  const result = renderWithProvider(
    <table>
      <thead>
        <tr>
          <SortableTh
            label="active days"
            sortKey="activeDays"
            activeKey="score"
            descending={false}
            onSort={onSort}
            numeric
            defaultDescending
            infoTitle="Active days"
            info={INFO}
          >
            Active days
          </SortableTh>
        </tr>
      </thead>
    </table>,
  );
  return { ...result, onSort };
}

describe('SortableTh definitions', () => {
  /**
   * The defect this guards.
   *
   * The column definition used to be passed in as `children`, which put an `InfoTip` - itself a
   * button - inside the sort button. Nested buttons are invalid HTML: the browser hoists the inner
   * one out, and every click aimed at the "i" landed on the sort handler and re-sorted the table
   * instead of opening the definition. On a page whose premise is "every figure carries its
   * definition", a definition that cannot be opened is a real defect.
   */
  it('does not nest the info button inside the sort button', () => {
    const { container } = renderHeader();

    const sortButton = screen.getByTitle('Sort by active days');
    const infoButton = screen.getByRole('button', { name: /How "Active days" is calculated/ });

    expect(sortButton).not.toBe(infoButton);
    expect(sortButton.contains(infoButton)).toBe(false);
    expect(container.querySelector('button button')).toBeNull();
  });

  it('opens the definition on click without re-sorting the column', async () => {
    const user = userEvent.setup();
    const { onSort } = renderHeader();

    await user.click(screen.getByRole('button', { name: /How "Active days" is calculated/ }));

    expect(onSort).not.toHaveBeenCalled();
    expect(await screen.findByText(INFO.what)).toBeInTheDocument();
  });

  it('still sorts when the header itself is clicked', async () => {
    const user = userEvent.setup();
    const { onSort } = renderHeader();

    await user.click(screen.getByTitle('Sort by active days'));

    // Numeric columns open descending: starting ascending shows the reader a screen of zeroes.
    expect(onSort).toHaveBeenCalledWith('activeDays', true);
  });
});

describe('RationaleCell', () => {
  const LONG =
    'Recommended - proven demand: already using Copilot Chat without a licence (21 interactions '
    + 'across 7 days); 57 Teams messages and 2 meetings; 33 emails sent, 68 read; 27 files viewed '
    + 'or edited.';

  it('keeps the whole sentence reachable while clamping what is drawn', () => {
    renderWithProvider(<RationaleCell text={LONG} />);

    // Rendered in full these cells wrapped to fifteen lines in a squeezed column and set the height
    // of every row in the table. The text must still be there in full - it is the justification an
    // admin pastes into a licence request - so it is clamped visually and carried on hover.
    const cell = screen.getByText(LONG);
    expect(cell).toHaveAttribute('title', LONG);
  });

  it('renders an em dash rather than an empty cell when there is no explanation', () => {
    renderWithProvider(<RationaleCell text="" />);

    expect(screen.getByText('\u2014')).toBeInTheDocument();
  });
});
