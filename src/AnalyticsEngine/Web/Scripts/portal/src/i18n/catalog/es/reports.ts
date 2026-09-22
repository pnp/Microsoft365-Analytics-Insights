import type { reports as en } from '../en/reports';

/**
 * Spanish (es-ES) text for the Reports page.
 *
 * Typed against the English module, so a key added there without a translation here fails the
 * build rather than reaching a customer as English text inside a Spanish page.
 */
const reports: Record<keyof typeof en, string> = {
  // Encabezado e introducción de página
  'reports.title': 'Informes',
  'reports.intro.beforeLicenceActivity': 'Vista rápida e integrada de la tendencia de uso de Microsoft 365. Los gráficos de informes solo aparecen cuando se están importando sus datos. Para asignaciones de licencias y actividad, abra',
  'reports.intro.licenceActivityLink': 'Actividad de licencias',
  'reports.intro.afterLicenceActivity': 'en la navegación de Insights.',
  'reports.empty.noImports': 'Aún no hay gráficos de informes integrados disponibles porque no hay importaciones de datos habilitadas. Habilite una o varias importaciones (Copilot, informes de uso, actividad de SharePoint, tráfico del sitio web, llamadas de Teams o correos electrónicos) en el instalador para verlos.',
  'reports.loading.reports': 'Cargando informes...',
  'reports.error.loadAreas': 'No se han podido cargar las áreas de informes.',

  // Áreas de informes
  'reports.area.copilot.label': 'Copilot',
  'reports.area.copilot.blurb': 'Adopción y uso de Microsoft 365 Copilot.',
  'reports.area.copilotAgents.label': 'Agentes de Copilot',
  'reports.area.copilotAgents.blurb': 'Popularidad y uso de los agentes de Copilot.',
  'reports.area.usage.label': 'Uso de Microsoft 365',
  'reports.area.usage.blurb': 'Usuarios activos semanales en las cargas de trabajo de Microsoft 365.',
  'reports.area.officeApps.label': 'Aplicaciones de Office',
  'reports.area.officeApps.blurb': 'Qué aplicaciones de Office usan las personas, en qué plataformas, en qué departamentos y hasta dónde ha llegado Copilot. Cada cifra cuenta personas, no acciones: el informe de Microsoft que la respalda registra quién usó una aplicación, nunca cuánto.',
  'reports.area.spoAudit.label': 'SharePoint y OneDrive',
  'reports.area.spoAudit.blurb': 'Actividad de archivos desde el registro de auditoría.',
  'reports.area.webTraffic.label': 'Tráfico del sitio web',
  'reports.area.webTraffic.blurb': 'Vistas de página y visitantes desde el rastreador de páginas.',
  'reports.area.calls.label': 'Llamadas de Teams',
  'reports.area.calls.blurb': 'Volumen y duración de llamadas de Teams.',
  'reports.area.emails.label': 'Correos electrónicos',
  'reports.area.emails.blurb': 'Volumen de correos electrónicos enviados.',

  // Controles de periodo
  'reports.period.label': 'Periodo',
  'reports.period.ariaLabel': 'Periodo de informe',
  'reports.period.lastMonth': 'Último mes',
  'reports.period.last3Months': 'Últimos 3 meses',
  'reports.period.last6Months': 'Últimos 6 meses',

  // Filtros de agentes de Copilot
  'reports.topAgents.label': 'Agentes principales',
  'reports.topAgents.ariaLabel': 'Número de agentes de Copilot principales',
  'reports.topAgents.filterPlaceholder': 'Filtrar por nombre de agente',
  'reports.topAgents.filterAriaLabel': 'Filtrar agentes de Copilot por nombre',

  // Vista de área de informes
  'reports.loading.charts': 'Cargando gráficos...',
  'reports.error.loadReport': 'No se ha podido cargar el informe.',
  'reports.areaHeader.usageLag': '{blurb} Semanas desde {from}. Los informes de uso llegan con unos días de retraso, por lo que las semanas más recientes aparecen cuando llega su informe.',
  'reports.areaHeader.toNow': '{blurb} Semanas desde {from} hasta ahora.',
  'reports.callsInfo.beforeTeamsExplorer': 'Este es solo el volumen principal de llamadas. Para ver el tamaño y la duración de las reuniones, los patrones por hora del día, las modalidades, la concentración por organizador y la calidad de las llamadas, consulte',
  'reports.callsInfo.teamsExplorerLink': 'Explorador de Teams',
  'reports.clampedWindow': 'Se muestran los últimos {current} meses en lugar de {selected}. Este informe lee un registro por persona y día, por lo que no se puede generar una ventana más larga a tiempo en un inquilino grande.',
  'reports.promptInsights.notConfigured': 'Los datos de solicitudes (frases comunes, sentimiento semanal e idioma de las solicitudes) no se muestran porque Azure AI Language no está configurado. Esos tres gráficos se crean a partir del enriquecimiento cognitivo del historial de solicitudes de Copilot, así que sin él siempre estarían vacíos. Añada un punto de conexión y una clave de Cognitive Services en el instalador y vuelva a ejecutar la importación del historial de interacciones de Copilot para habilitarlos.',
  'reports.chart.sqlTitle': 'SQL de este gráfico',
  'reports.chart.loadError': 'No se ha podido cargar este gráfico: {error}',
  'reports.chart.noData': 'Sin datos para este periodo.',
};

export default reports;
