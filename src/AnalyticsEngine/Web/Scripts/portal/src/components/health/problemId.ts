/**
 * App Insights problem ids are `<exception type> at <method>`, and the Top exception types table
 * already shows the type in its own column - so strip that prefix rather than rendering ~35
 * redundant characters in the widest column.
 *
 * Also demangles the compiler-generated async state machine that dominates these strings:
 * `Foo+<BarAsync>d__3` + backtick + `1.MoveNext` becomes `Foo.BarAsync`. The raw, untouched value
 * is kept in the cell's tooltip so nothing is lost for anyone searching App Insights for it.
 *
 * Pure string logic, deliberately in its own module so it can be exercised without a DOM.
 */
export function shortenProblemId(problemId: string | null, type: string | null): string {
  if (!problemId) return '';

  let text = problemId;
  if (type && text.startsWith(`${type} at `)) {
    text = text.slice(type.length + 4);
  }

  // Foo+<BarAsync>d__3`1.MoveNext  ->  Foo.BarAsync   (the `1 arity suffix is optional)
  text = text.replace(/\+<([^>]+)>d__\d+(?:`\d+)?\.MoveNext/g, '.$1');

  return text;
}
