import type { charts as en } from '../en/charts';

/**
 * Spanish (es-ES) text for the shared chart components in src/components/charts.
 *
 * Typed against the English module, so a key added there without a translation here fails the
 * build rather than reaching a customer as English text inside a Spanish page.
 */
const charts: Record<keyof typeof en, string> = {
  // Shared chart states and legends
  'charts.empty.noDataForPeriod': 'Sin datos para este periodo.',
  'charts.legend.none': 'Ninguno',
  'charts.legend.maxValue': '{value} {valueLabel}',

  // Category bars
  'charts.categoryBar.rowTitle': '{label}: {value} {valueLabel}',

  // Donut chart
  'charts.donut.sliceTitle': '{label}: {value} ({percent} %)',
  'charts.donut.legendValue': '{value} ({percent} %)',

  // Gauge chart
  'charts.gauge.ariaLabel': '{label}: {percent} %',
  'charts.gauge.needsAttention': 'Requiere atención',
  'charts.gauge.progressing': 'En curso',
  'charts.gauge.healthy': 'Correcto',

  // Heatmap chart
  'charts.heatmap.cellTitle': '{day} {hour}:00 - {value} {valueLabel}',

  // Matrix chart
  'charts.matrix.cellTitle': '{row} / {column}: {value} {valueLabel}',
  'charts.matrix.legendShadedByRow': '{columnLabel} \u00b7 sombreado dentro de cada fila \u00b7 ninguno',
  'charts.matrix.legendShadedOverall': '{columnLabel} \u00b7 ninguno',
  'charts.matrix.most': 'máximo',

  // Radar chart
  'charts.radar.notEnoughData': 'No hay suficientes datos para trazar el gráfico.',
  'charts.radar.ariaLabel': 'Perfil de componentes de interacción',
  'charts.radar.pointTitle': '{series} - {axis}: {value}',
  'charts.radar.gap': 'Diferencia',
  'charts.radar.scaleNote': 'Los {count} componentes están en una escala de 0 a {max} y comparten la misma escala, por lo que los dos contornos son directamente comparables. Lea la tabla para ver los tamaños: el área de un radar crece con el cuadrado de sus valores, por lo que la forma exagera la diferencia.',

  // Sankey chart
  'charts.sankey.empty': 'No hay flujos de visitas para este periodo.',
  'charts.sankey.ariaLabel': 'Diagrama de Sankey de dónde empezaron y terminaron las visitas, medido en {valueLabel}.',
  'charts.sankey.flowTitle': '{source} \u2192 {target}: {value} {valueLabel}',
  'charts.sankey.nodeTitle': '{label}: {value} {valueLabel}',
  'charts.sankey.tableCaption': 'Dónde empezaron y terminaron las visitas',
  'charts.sankey.startedOn': 'Empezó en',
  'charts.sankey.endedOn': 'Terminó en',
  'charts.sankey.samePage': 'la misma página',

  // Stacked area chart
  'charts.stackedArea.ariaLabel': '{valueLabel} a lo largo del tiempo por población',
  'charts.stackedArea.weekOf': 'Semana de {week}',
  'charts.stackedArea.total': 'Total general: {value}',

  // Time series chart
  'charts.timeSeries.ariaLabel': '{valueLabel} por semana',
  'charts.timeSeries.noData': 'Sin datos',

  // Treemap chart
  'charts.treemap.ariaLabel': '{valueLabel} por categoría',
  'charts.treemap.tileTitle': '{label}: {value} {valueLabel} ({percent} %)',

  // Word cloud
  'charts.wordCloud.wordTitle': '{label}: {value} {valueLabel}',
};

export default charts;
