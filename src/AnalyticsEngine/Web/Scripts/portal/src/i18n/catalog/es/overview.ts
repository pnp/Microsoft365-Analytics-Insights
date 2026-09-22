import type { overview as en } from '../en/overview';

/**
 * Spanish (es-ES) text for the Insights overview page and its tiles.
 *
 * Typed against the English module, so a key added there without a translation here fails the
 * build rather than reaching a customer as English text inside a Spanish page.
 */
const overview: Record<keyof typeof en, string> = {
  // Overview page
  'overview.page.loadError': 'No se pudo cargar el resumen de datos.',
  'overview.page.unknownError': 'error desconocido',
  'overview.page.loading': 'Cargando resumen de datos...',
  'overview.page.noDataAvailable': 'No hay ningún resumen de datos disponible.',
  'overview.page.title': 'Resumen',
  'overview.page.lede': 'Microsoft 365 Advanced Analytics recopila actividad de todo el inquilino en su propia base de datos. Aquí se muestra qué contiene, si sigue llegando y dónde ir a continuación.',
  'overview.page.yourDataHeading': 'Sus datos',
  'overview.page.importSettingsKnown': 'Solo se muestran las cargas de trabajo activadas para esta implementación.',
  'overview.page.importSettingsUnknown': 'No se pudo leer la configuración de importación, por lo que se muestran todas las cifras.',
  'overview.page.noImportsPrefix': 'No hay importaciones activadas para esta implementación, así que todavía no hay nada que resumir. Actívelas en el instalador y después compruebe',
  'overview.page.serviceHealthLink': 'Administración \u2192 Estado del servicio',
  'overview.page.zeroFiguresPrefix': 'Todas las cifras siguen en cero. Es normal durante las primeras horas después de una instalación; si persiste, compruebe',
  'overview.page.zeroFiguresSuffix': 'para ver si las importaciones se están ejecutando.',
  'overview.page.importsSwitchedOn': 'Importaciones activadas:',
  'overview.page.whereToNextHeading': 'Ir a continuación',
  'overview.page.whereToNextNote': 'Las partes del portal que se aplican a esta implementación.',

  // Health snapshot
  'overview.healthSnapshot.title': 'Estado del sistema',
  'overview.healthSnapshot.checking': 'Comprobando...',
  'overview.healthSnapshot.openServiceHealth': 'Abrir Estado del servicio',
  'overview.healthSnapshot.summaryError': 'No se pudo leer el resumen de estado ({error}). Las cifras anteriores proceden directamente de la base de datos y no se ven afectadas.',
  'overview.healthSnapshot.newest': '{name} más reciente',
  'overview.healthSnapshot.auditEventName': 'evento de auditoría',
  'overview.healthSnapshot.webPageHitName': 'visita a página web',
  'overview.healthSnapshot.auditEventsLast24h': 'Eventos de auditoría en las últimas 24 h',
  'overview.healthSnapshot.webPageHitsLast24h': 'Visitas a páginas web en las últimas 24 h',
  'overview.healthSnapshot.recentVolumeWarning': 'El análisis de frescura y volumen de 24 h no terminó en esta base de datos, por lo que esas cifras muestran "-". Esto es esperado en inquilinos muy grandes.',
  'overview.healthSnapshot.checkingFreshness': 'Comprobando la antigüedad de los datos importados...',
  'overview.status.degraded': 'Degradado',
  'overview.status.healthy': 'Correcto',
  'overview.status.unknown': 'Desconocido',
  'overview.status.unhealthy': 'Erróneo',

  // Where to next
  'overview.whereToNext.reports.title': 'Informes',
  'overview.whereToNext.reports.blurb': 'Actividad de gráficos, uso de Copilot y tráfico de páginas a lo largo del tiempo, segmentado por departamento o sitio.',
  'overview.whereToNext.copilotAdoption.title': 'Adopción de Copilot',
  'overview.whereToNext.copilotAdoption.blurb': 'Quién obtiene valor de su licencia de Copilot y quién ha dejado de usarla.',
  'overview.whereToNext.licenceActivity.title': 'Actividad de licencias',
  'overview.whereToNext.licenceActivity.blurb': 'Licencias por las que se paga frente a la actividad realmente observada, servicio por servicio.',
  'overview.whereToNext.agentCosts.title': 'Costes de agentes',
  'overview.whereToNext.agentCosts.blurb': 'Créditos facturados de Copilot Studio y gasto de Azure, atribuidos por agente.',
  'overview.whereToNext.dlp.title': 'Impacto de DLP',
  'overview.whereToNext.dlp.blurb': 'Dónde las directivas de prevención de pérdida de datos de Purview bloquean a personas y agentes.',
  'overview.whereToNext.teamsPermissions.title': 'Permisos de Teams',
  'overview.whereToNext.teamsPermissions.blurb': 'Active el análisis de Teams a nivel de canal, un equipo cada vez.',
  'overview.whereToNext.health.title': 'Estado del servicio',
  'overview.whereToNext.health.blurb': 'Actividad de importación, excepciones, estado de componentes y frescura de la base de datos.',
  'overview.whereToNext.configuration.title': 'Configuración del servicio',
  'overview.whereToNext.configuration.blurb': 'Qué importaciones están activadas, la versión del esquema de base de datos y los servicios conectados.',
};

export default overview;
