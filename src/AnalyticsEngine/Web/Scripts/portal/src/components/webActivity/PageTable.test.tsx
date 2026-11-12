import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';

import { renderWithProvider } from '../../test/renderWithProvider';
import PageTable, { pageHref } from './PageTable';
import type { WebActivityPageRow } from '../../types/webActivity';

function row(url: string, title: string): WebActivityPageRow {
  return {
    title,
    url,
    site: 'Contoso Intranet',
    pageViews: 10,
    uniquePageViews: 5,
    visitors: 4,
    averageSecondsOnPage: 30,
    averageLoadSeconds: 1.2,
    entries: 2,
    exits: 1,
    bounces: 1,
    bouncePct: 50,
  };
}

describe('PageTable links', () => {
  it('links a SharePoint page by its http(s) address', () => {
    renderWithProvider(
      <PageTable label="Pages" rows={[row('https://contoso.sharepoint.com/sites/news/SitePages/Home.aspx', 'Contoso news')]} />,
    );

    expect(screen.getByRole('link', { name: 'Contoso news' })).toHaveAttribute(
      'href',
      'https://contoso.sharepoint.com/sites/news/SitePages/Home.aspx',
    );
  });

  it('shows a page whose address is not http(s) as text, never as a link', () => {
    renderWithProvider(
      <PageTable
        label="Pages"
        rows={[row('javascript:alert(document.cookie)', 'Contoso script'), row('data:text/html,<b>x</b>', 'Contoso data')]}
      />,
    );

    expect(screen.queryAllByRole('link')).toHaveLength(0);
    expect(screen.getByText('Contoso script')).toBeInTheDocument();
    expect(screen.getByText('Contoso data')).toBeInTheDocument();
  });

  it('accepts only http and https', () => {
    expect(pageHref('https://contoso.sharepoint.com/a.aspx')).toBe('https://contoso.sharepoint.com/a.aspx');
    expect(pageHref('http://contoso.local/a.aspx')).toBe('http://contoso.local/a.aspx');
    expect(pageHref(' JavaScript:alert(1)')).toBeNull();
    expect(pageHref('vbscript:msgbox(1)')).toBeNull();
    expect(pageHref('/relative/page.aspx')).toBeNull();
    expect(pageHref('')).toBeNull();
    expect(pageHref(null)).toBeNull();
  });
});
