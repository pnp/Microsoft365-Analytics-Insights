import { describe, it, expect, vi } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../../test/renderWithProvider';
import {
  DetailRationale,
  DetailRow,
  DetailSection,
  DetailSections,
  DetailStat,
  DetailStats,
  ExpandAllButton,
  ExpandableUserCell,
  PartialPrintNote,
  PrintedFilters,
  SortableTh,
  printedSearch,
  useRowExpansion,
} from './adoptionShared';
import { PRINT_ROW_LIMIT } from '../shared/printPreparation';

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

describe('expandable rows', () => {
  const LONG =
    'Recommended - proven demand: already using Copilot Chat without a licence (21 interactions '
    + 'across 7 days); 57 Teams messages and 2 meetings; 33 emails sent, 68 read; 27 files viewed '
    + 'or edited.';

  const ALICE = 'demo.user0000053@contoso.example';
  const BOB = 'demo.user0000094@contoso.example';

  function Row({ id, upn }: { id: number; upn: string }) {
    const { isExpanded, toggle } = useRowExpansion();
    const open = isExpanded(id);
    return (
      <table>
        <tbody>
          <tr>
            <ExpandableUserCell
              open={open}
              onToggle={() => toggle(id)}
              userPrincipalName={upn}
              secondary="VP of Sales"
            />
            <td>Executive</td>
          </tr>
          {open && (
            <DetailRow colSpan={2}>
              <DetailSections>
                <DetailSection title="Coordination load - 86/100">
                  <DetailStats>
                    <DetailStat label="Meetings" value={3} sub="per day - 86/100 vs target 4" />
                  </DetailStats>
                </DetailSection>
              </DetailSections>
              <DetailSection title="Justification">
                <DetailRationale text={LONG} />
              </DetailSection>
            </DetailRow>
          )}
        </tbody>
      </table>
    );
  }

  function Two() {
    const { isExpanded, toggle } = useRowExpansion();
    return (
      <table>
        <tbody>
          {[
            { id: 1, upn: ALICE },
            { id: 2, upn: BOB },
          ].map((r) => (
            <tr key={r.id}>
              <ExpandableUserCell
                open={isExpanded(r.id)}
                onToggle={() => toggle(r.id)}
                userPrincipalName={r.upn}
              />
              <td>{isExpanded(r.id) ? `detail for ${r.upn}` : ''}</td>
            </tr>
          ))}
        </tbody>
      </table>
    );
  }

  it('starts collapsed and hides the detail', () => {
    renderWithProvider(<Row id={7} upn={ALICE} />);

    expect(screen.getByRole('button', { name: new RegExp(`Show the full assessment for ${ALICE}`) }))
      .toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByText(LONG)).not.toBeInTheDocument();
  });

  /**
   * The defect this guards.
   *
   * The justification used to be a column, clamped to two lines with the rest on hover, in a table
   * already wider than the screen - so on the Cowork and Opportunities tables the one sentence
   * written to be read was both truncated on every row and scrolled off the right-hand edge. Opened
   * here it must be present IN FULL, not clamped and not hidden behind a title attribute.
   */
  it('reveals the whole justification and the figures behind the score when expanded', async () => {
    const user = userEvent.setup();
    renderWithProvider(<Row id={7} upn={ALICE} />);

    await user.click(screen.getByRole('button', { name: /Show the full assessment/ }));

    const rationale = await screen.findByText(LONG);
    expect(rationale).toBeInTheDocument();
    expect(rationale).not.toHaveAttribute('title');

    expect(screen.getByText('Coordination load - 86/100')).toBeInTheDocument();
    expect(screen.getByText('per day - 86/100 vs target 4')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Hide the full assessment/ })).toHaveAttribute(
      'aria-expanded',
      'true',
    );
  });

  it('collapses again on a second click', async () => {
    const user = userEvent.setup();
    renderWithProvider(<Row id={7} upn={ALICE} />);

    await user.click(screen.getByRole('button', { name: /Show the full assessment/ }));
    await user.click(screen.getByRole('button', { name: /Hide the full assessment/ }));

    expect(screen.queryByText(LONG)).not.toBeInTheDocument();
  });

  /** Keyed by user id, so a re-sort cannot leave a different person's detail open. */
  it('expands only the row that was clicked', async () => {
    const user = userEvent.setup();
    renderWithProvider(<Two />);

    await user.click(screen.getByRole('button', { name: new RegExp(`assessment for ${ALICE}`) }));

    expect(screen.getByText(`detail for ${ALICE}`)).toBeInTheDocument();
    expect(screen.queryByText(`detail for ${BOB}`)).not.toBeInTheDocument();
  });

  it('renders an em dash rather than an empty panel when there is no explanation', () => {
    renderWithProvider(<DetailRationale text="" />);

    expect(screen.getByText('\u2014')).toBeInTheDocument();
  });

  it('keeps its expand chevron off paper, where it cannot be pressed', () => {
    renderWithProvider(<Row id={7} upn={ALICE} />);

    expect(screen.getByRole('button', { name: /Show the full assessment/ })).toHaveAttribute('data-print', 'hide');
    // The person the row is about stays on the printout.
    expect(screen.getByText(ALICE).closest('[data-print="hide"]')).toBeNull();
  });
});

describe('expand all', () => {
  const PEOPLE = [
    { id: 1, upn: 'demo.user0000001@contoso.example' },
    { id: 2, upn: 'demo.user0000002@contoso.example' },
    { id: 3, upn: 'demo.user0000003@contoso.example' },
  ];

  /**
   * A list that can change which rows it shows, as paging and filtering do - so the tests can prove
   * that "expand all" means the whole list and not just the rows it was clicked over.
   */
  function List({ ids }: { ids: number[] }) {
    const { isExpanded, toggle, expandAll, collapseAll, allExpanded, resetRows } = useRowExpansion();
    return (
      <>
        <ExpandAllButton allExpanded={allExpanded} onExpandAll={expandAll} onCollapseAll={collapseAll} />
        <button type="button" onClick={resetRows}>
          next page
        </button>
        <table>
          <tbody>
            {PEOPLE.filter((p) => ids.includes(p.id)).map((p) => (
              <tr key={p.id}>
                <ExpandableUserCell open={isExpanded(p.id)} onToggle={() => toggle(p.id)} userPrincipalName={p.upn} />
                <td>{isExpanded(p.id) ? `detail for ${p.upn}` : ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </>
    );
  }

  const openDetails = () => screen.queryAllByText(/^detail for /).length;

  it('opens every row, and turns into the control that closes them', async () => {
    const user = userEvent.setup();
    renderWithProvider(<List ids={[1, 2, 3]} />);

    await user.click(screen.getByRole('button', { name: 'Expand all' }));

    expect(openDetails()).toBe(3);
    await user.click(screen.getByRole('button', { name: 'Collapse all' }));
    expect(openDetails()).toBe(0);
  });

  it('opens rows that were not on screen when it was clicked', async () => {
    // A mode, not a list of ids: the next page - and a printout, which loads the whole list - has to
    // arrive open too, or "expand all" only ever meant the fifty rows under the pointer.
    const user = userEvent.setup();
    const { rerender } = renderWithProvider(<List ids={[1]} />);

    await user.click(screen.getByRole('button', { name: 'Expand all' }));
    rerender(<List ids={[1, 2, 3]} />);

    expect(openDetails()).toBe(3);
  });

  it('lets one row be closed without collapsing the rest', async () => {
    const user = userEvent.setup();
    renderWithProvider(<List ids={[1, 2, 3]} />);

    await user.click(screen.getByRole('button', { name: 'Expand all' }));
    await user.click(screen.getByRole('button', { name: /Hide the full assessment for demo.user0000002/ }));

    expect(openDetails()).toBe(2);
    expect(screen.queryByText('detail for demo.user0000002@contoso.example')).toBeNull();
    // No longer everything, so the control offers to open everything again.
    await user.click(screen.getByRole('button', { name: 'Expand all' }));
    expect(openDetails()).toBe(3);
  });

  it('survives a page change, which only forgets rows toggled by hand', async () => {
    const user = userEvent.setup();
    renderWithProvider(<List ids={[1, 2, 3]} />);

    await user.click(screen.getByRole('button', { name: 'Expand all' }));
    await user.click(screen.getByRole('button', { name: /Hide the full assessment for demo.user0000002/ }));
    await user.click(screen.getByRole('button', { name: 'next page' }));

    expect(openDetails()).toBe(3);
    expect(screen.getByRole('button', { name: 'Collapse all' })).toBeTruthy();
  });

  it('closes a row opened by hand when the page changes, as it always has', async () => {
    const user = userEvent.setup();
    renderWithProvider(<List ids={[1, 2, 3]} />);

    await user.click(screen.getByRole('button', { name: /Show the full assessment for demo.user0000001/ }));
    await user.click(screen.getByRole('button', { name: 'next page' }));

    expect(openDetails()).toBe(0);
  });

  it('is not printed itself', () => {
    renderWithProvider(<List ids={[1]} />);

    expect(screen.getByRole('button', { name: 'Expand all' })).toHaveAttribute('data-print', 'hide');
  });
});

describe('printed filters', () => {
  it('states each setting on paper, and nothing on screen', () => {
    renderWithProvider(
      <PrintedFilters
        filters={[
          { label: 'Department', value: 'Customer Support' },
          { label: 'Sorted by', value: 'Most coordination load' },
          { value: 'Policy list only' },
          false,
          null,
        ]}
      />,
    );

    const line = document.querySelector('[data-print="only"]');
    expect(line?.textContent).toBe(
      'Filters: Department: Customer Support \u00b7 Sorted by: Most coordination load \u00b7 Policy list only',
    );
  });

  it('prints nothing when there is nothing to state', () => {
    renderWithProvider(<PrintedFilters filters={[false, null, undefined]} />);

    expect(document.querySelector('[data-print="only"]')).toBeNull();
  });

  it('quotes an applied search and leaves out an empty one', () => {
    const t = (key: string, values?: Record<string, unknown>) =>
      key === 'copilotAdoption.shared.printedFilters.searchTerm' ? `"${values?.term}"` : key;

    expect(printedSearch(t as never, '  finance  ')).toEqual({
      label: 'copilotAdoption.shared.printedFilters.search',
      value: '"finance"',
    });
    expect(printedSearch(t as never, '   ')).toBeNull();
  });
});

describe('partial print note', () => {
  it('says a printed list is one page of a longer one', () => {
    renderWithProvider(<PartialPrintNote shownRows={50} totalRows={600} />);

    const note = document.querySelector('[data-print="only"]');
    expect(note?.textContent).toContain('only the rows that were on screen');
    expect(note?.textContent).toContain(PRINT_ROW_LIMIT.toLocaleString('en'));
  });

  it('says nothing when the whole list is on the page', () => {
    renderWithProvider(<PartialPrintNote shownRows={600} totalRows={600} />);

    expect(document.querySelector('[data-print="only"]')).toBeNull();
  });
});

describe('SortableTh on paper', () => {
  function header(activeKey: string) {
    return renderWithProvider(
      <table>
        <thead>
          <tr>
            <SortableTh label="user" sortKey="upn" activeKey={activeKey} descending onSort={vi.fn()}>
              User
            </SortableTh>
          </tr>
        </thead>
      </table>,
    );
  }

  it('keeps the arrow of the column the list is sorted by, which says how the rows are ordered', () => {
    const { container } = header('upn');

    expect(container.querySelector('th [aria-hidden="true"]')).not.toHaveAttribute('data-print');
  });

  it('drops the faint arrows that only say a header is clickable', () => {
    const { container } = header('score');

    expect(container.querySelector('th [aria-hidden="true"]')).toHaveAttribute('data-print', 'hide');
    expect(screen.getByText('User').closest('[data-print="hide"]')).toBeNull();
  });
});
