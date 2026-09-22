/**
 * English text for the shared chart components in src/components/charts.
 *
 * Every key here must have a Spanish counterpart in `../es/charts.ts`; the type of that module
 * makes a missing one a build failure.
 */
export const charts = {
  // Shared chart states and legends
  'charts.empty.noDataForPeriod': 'No data for this period.',
  'charts.legend.none': 'None',
  'charts.legend.maxValue': '{value} {valueLabel}',

  // Category bars
  'charts.categoryBar.rowTitle': '{label}: {value} {valueLabel}',

  // Donut chart
  'charts.donut.sliceTitle': '{label}: {value} ({percent}%)',
  'charts.donut.legendValue': '{value} ({percent}%)',

  // Gauge chart
  'charts.gauge.ariaLabel': '{label}: {percent}%',
  'charts.gauge.needsAttention': 'Needs attention',
  'charts.gauge.progressing': 'Progressing',
  'charts.gauge.healthy': 'Healthy',

  // Heatmap chart
  'charts.heatmap.cellTitle': '{day} {hour}:00 - {value} {valueLabel}',

  // Matrix chart
  'charts.matrix.cellTitle': '{row} / {column}: {value} {valueLabel}',
  'charts.matrix.legendShadedByRow': '{columnLabel} \u00b7 shaded within each row \u00b7 none',
  'charts.matrix.legendShadedOverall': '{columnLabel} \u00b7 none',
  'charts.matrix.most': 'most',

  // Radar chart
  'charts.radar.notEnoughData': 'Not enough data to plot.',
  'charts.radar.ariaLabel': 'Engagement component profile',
  'charts.radar.pointTitle': '{series} - {axis}: {value}',
  'charts.radar.gap': 'Gap',
  'charts.radar.scaleNote': 'All {count} components are 0-{max} and share one scale, so the two outlines are directly comparable. Read the table for the sizes - the area of a radar grows with the square of its values, so the shape overstates the difference.',

  // Sankey chart
  'charts.sankey.empty': 'No visit flows for this period.',
  'charts.sankey.ariaLabel': 'Sankey diagram of where visits started and ended, measured in {valueLabel}.',
  'charts.sankey.flowTitle': '{source} \u2192 {target}: {value} {valueLabel}',
  'charts.sankey.nodeTitle': '{label}: {value} {valueLabel}',
  'charts.sankey.tableCaption': 'Where visits started and ended',
  'charts.sankey.startedOn': 'Started on',
  'charts.sankey.endedOn': 'Ended on',
  'charts.sankey.samePage': 'the same page',

  // Stacked area chart
  'charts.stackedArea.ariaLabel': '{valueLabel} over time by population',
  'charts.stackedArea.weekOf': 'Week of {week}',
  'charts.stackedArea.total': 'Total: {value}',

  // Time series chart
  'charts.timeSeries.ariaLabel': '{valueLabel} per week',
  'charts.timeSeries.noData': 'No data',

  // Treemap chart
  'charts.treemap.ariaLabel': '{valueLabel} by category',
  'charts.treemap.tileTitle': '{label}: {value} {valueLabel} ({percent}%)',

  // Word cloud
  'charts.wordCloud.wordTitle': '{label}: {value} {valueLabel}',
} as const;

export default charts;
