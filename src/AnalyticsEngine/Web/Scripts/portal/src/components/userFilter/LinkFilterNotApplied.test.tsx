import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import { GlobalFilterProvider } from '../globalFilter/GlobalFilterProvider';
import LinkFilterNotApplied from './LinkFilterNotApplied';
import { EMPTY_USER_FILTER, type UserFilter } from '../../types/userFilter';

const CONTOSO_SALES: UserFilter = {
  clauses: [{ join: 'and', dimension: 'department', operator: 'is', values: ['Contoso Sales'], includeNotSet: false }],
};

const NO_PII = { administration: false, seePii: false };

describe('LinkFilterNotApplied', () => {
  it('tells a reader without See PII that the link’s filter was not applied, and that the figures are for everyone', () => {
    renderWithProvider(<LinkFilterNotApplied requested={CONTOSO_SALES} />, { access: NO_PII });

    expect(screen.getByText('The filter in this link was not applied')).toBeInTheDocument();
    expect(screen.getByText(/needs the See PII permission/)).toBeInTheDocument();
    expect(screen.getByText(/for everyone this report covers, not only the people the link was filtered to/)).toBeInTheDocument();
  });

  it('names the administrator’s report filter as the population when one is applied', () => {
    renderWithProvider(
      <GlobalFilterProvider
        value={{ effective: { active: true, applied: true, bypassed: false, canBypass: false, revision: 1, filter: null, invalid: false } }}
      >
        <LinkFilterNotApplied requested={CONTOSO_SALES} />
      </GlobalFilterProvider>,
      { access: NO_PII },
    );

    expect(screen.getByText(/for everyone within the administrator’s report filter/)).toBeInTheDocument();
    expect(screen.queryByText(/for everyone this report covers/)).not.toBeInTheDocument();
  });

  it('says nothing when the filter was applied, or when there was none', () => {
    const { unmount } = renderWithProvider(<LinkFilterNotApplied requested={CONTOSO_SALES} />);
    expect(screen.queryByText('The filter in this link was not applied')).not.toBeInTheDocument();
    unmount();

    renderWithProvider(<LinkFilterNotApplied requested={EMPTY_USER_FILTER} />, { access: NO_PII });
    expect(screen.queryByText('The filter in this link was not applied')).not.toBeInTheDocument();
  });
});
