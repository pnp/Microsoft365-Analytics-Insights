// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Every report in Insights applies the administrator's global filter on the server, and every page in
 * Insights must say so - a report narrowed by a filter the page never mentions is read as the whole
 * organisation. This reads the route table rather than keeping a list, so a page added to Insights
 * without the bar fails here instead of shipping.
 */
const SRC = join(process.cwd(), 'src');

function insightsPages(): { component: string; file: string }[] {
  const navigation = readFileSync(join(SRC, 'navigation.tsx'), 'utf8');

  const files = new Map<string, string>();
  for (const match of navigation.matchAll(/const (\w+) = lazyWithReload\(\(\) => import\('\.\/pages\/(\w+)'\)\)/g)) {
    files.set(match[1], match[2]);
  }

  const pages: { component: string; file: string }[] = [];
  for (const match of navigation.matchAll(/\{\s*area: 'insights',[\s\S]*?element: <(\w+) \/>/g)) {
    const file = files.get(match[1]);
    expect(file, `No lazy import found for ${match[1]}`).toBeDefined();
    pages.push({ component: match[1], file: file! });
  }
  return pages;
}

describe('the global filter on every Insights page', () => {
  it('finds the Insights pages in the route table', () => {
    expect(insightsPages().length).toBeGreaterThanOrEqual(8);
  });

  it('shows the bar on every one of them', () => {
    const missing = insightsPages()
      .filter(({ file }) => !readFileSync(join(SRC, 'pages', `${file}.tsx`), 'utf8').includes('<GlobalFilterBar'))
      .map(({ file }) => file);

    expect(missing, 'Insights pages that never show the administrator’s global filter').toEqual([]);
  });
});
