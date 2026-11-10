import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import { loadCatalog } from '../../i18n';
import { JudgementList } from './webActivityShared';
import type { WebActivityJudgement } from '../../types/webActivity';

const reach: WebActivityJudgement = {
  key: 'reach',
  tone: 'warning',
  headlineKey: 'reach.headline.share',
  detailKey: 'reach.detail.moderate',
  values: { reach: 45.25, enabledVisitors: 12345, knownUsers: 27420 },
  headline: '45.3% of enabled directory users had a tracked visit (12,345 of 27,420)',
  detail: 'Server English detail.',
};

describe('JudgementList', () => {
  it('words a judgement from its key, with figures in the reader\u2019s number format', async () => {
    await loadCatalog('es');
    renderWithProvider(<JudgementList judgements={[reach]} />, { language: 'es' });

    expect(
      screen.getByText('El 45,3% de los usuarios habilitados del directorio tuvo una visita registrada (12.345 de 27.420)'),
    ).toBeInTheDocument();
    expect(screen.getByText(/^Una minoría considerable no tiene ninguna visita registrada\./)).toBeInTheDocument();
    expect(screen.queryByText(/enabled directory users/)).not.toBeInTheDocument();
    expect(screen.queryByText('Server English detail.')).not.toBeInTheDocument();
  });

  it('words it in English from the catalog too, rather than echoing the server', () => {
    renderWithProvider(<JudgementList judgements={[{ ...reach, detail: 'Stale server wording.' }]} />);

    expect(screen.getByText('45.3% of enabled directory users had a tracked visit (12,345 of 27,420)')).toBeInTheDocument();
    expect(screen.getByText(/^A substantial minority have no tracked visit\./)).toBeInTheDocument();
    expect(screen.queryByText('Stale server wording.')).not.toBeInTheDocument();
  });

  it('falls back to the server\u2019s English for a key this build does not know, or a figure that did not arrive', async () => {
    await loadCatalog('es');
    renderWithProvider(
      <JudgementList
        judgements={[
          { ...reach, key: 'newer', headlineKey: 'newer.headline', detailKey: 'newer.detail', headline: 'A newer verdict', detail: 'From a newer server.' },
          { ...reach, values: { reach: 'NaN' } },
          { key: 'bounce', tone: 'good', headline: 'From a server older than the keys', detail: 'Older detail.' },
        ]}
      />,
      { language: 'es' },
    );

    expect(screen.getByText('A newer verdict')).toBeInTheDocument();
    expect(screen.getByText('From a newer server.')).toBeInTheDocument();
    // Its headline needs counts that never arrived; a literal {enabledVisitors} is worse than English.
    expect(screen.getByText('45.3% of enabled directory users had a tracked visit (12,345 of 27,420)')).toBeInTheDocument();
    expect(screen.queryByText(/\{\w+\}/)).not.toBeInTheDocument();
    expect(screen.getByText('From a server older than the keys')).toBeInTheDocument();
  });
});
