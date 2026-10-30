import { pathToFileURL } from 'node:url';

// Each group excludes every preceding group, so new tests always run exactly once.
export function testShards() {
  const groups = [
    { name: 'demo', terms: ['Demo'] },
    { name: 'migrations', terms: ['Migration', 'Upgrade'] },
    { name: 'reports', terms: ['Copilot', 'Licence', 'License'] },
    { name: 'core', terms: [] },
  ];
  const excluded = [];
  return groups.map(({ name, terms }) => {
    const predicates = excluded.map(term => `FullyQualifiedName!~${term}`);
    if (terms.length) {
      predicates.push(`(${terms.map(term => `FullyQualifiedName~${term}`).join('|')})`);
    }
    excluded.push(...terms);
    return { name, filter: predicates.join('&') };
  });
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  console.log(JSON.stringify({ include: testShards() }));
}
