import type { overview as en } from '../en/overview';

/**
 * Spanish (es-ES) text for the Insights overview page and its tiles.
 *
 * Typed against the English module, so a key added there without a translation here fails the
 * build rather than reaching a customer as English text inside a Spanish page.
 */
const overview: Record<keyof typeof en, string> = {

  // Fichas de recuento de datos del resumen. El servidor redacta su propio texto en ingles
  // (SystemStatusAPIController), asi que la SPA asigna su clave estable a estas entradas.
  'overview.dataCount.users.name': 'Usuarios',
  'overview.dataCount.users.hint': 'Personas detectadas por cualquier importación',
  'overview.dataCount.auditEvents.name': 'Eventos de auditoría',
  'overview.dataCount.auditEvents.hint': 'Actividad del registro de auditoría unificado',
  'overview.dataCount.copilotInteractions.name': 'Interacciones de Copilot',
  'overview.dataCount.copilotInteractions.hint': 'Chats de Copilot del registro de auditoría',
  'overview.dataCount.copilotAiInteractions.name': 'Interacciones de IA de Copilot',
  'overview.dataCount.copilotAiInteractions.hint': 'Del historial de interacciones de IA de Graph',
  'overview.dataCount.webHits.name': 'Visitas a páginas web',
  'overview.dataCount.webHits.hint': 'Páginas vistas del rastreador de SharePoint',
  'overview.dataCount.trackedUrls.name': 'URL supervisadas',
  'overview.dataCount.trackedUrls.hint': 'Páginas distintas detectadas por el rastreador',
  'overview.dataCount.sharePointSites.name': 'Sitios de SharePoint',
  'overview.dataCount.sharePointSites.hint': 'Sitios detectados en la actividad o el tráfico web',
  'overview.dataCount.sentEmails.name': 'Correos enviados',
  'overview.dataCount.sentEmails.hint': 'Correo enviado, importado desde Graph',
  'overview.dataCount.teams.name': 'Equipos detectados',
  'overview.dataCount.teams.hint': 'Equipos encontrados en el inquilino',
  'overview.dataCount.teamsTracked.name': 'Equipos con seguimiento detallado',
  'overview.dataCount.teamsTracked.hint': 'Equipos que han concedido análisis por canal',
  'overview.dataCount.teamsCalls.name': 'Llamadas de Teams',
  'overview.dataCount.teamsCalls.hint': 'Registros de llamadas de Graph',
  'overview.dataCount.powerApps.name': 'Power Apps',
  'overview.dataCount.powerApps.hint': 'Aplicaciones detectadas en la actividad de Power Platform',
  'overview.dataCount.dlpMatches.name': 'Coincidencias de reglas DLP',
  'overview.dataCount.dlpMatches.hint': 'Reglas DLP de Purview activadas',
  'overview.dataCount.licenceTypes.name': 'SKU de licencia',
  'overview.dataCount.licenceTypes.hint': 'Tipos de licencia asignados en el inquilino',
  'overview.dataCount.copilotStudioCreditDays.name': 'Días de créditos de Copilot Studio',
  'overview.dataCount.copilotStudioCreditDays.hint': 'Créditos de agente facturados, por agente y día',
  'overview.dataCount.azureCostDays.name': 'Días de coste de Azure',
  'overview.dataCount.azureCostDays.hint': 'Gasto diario de Azure desde Cost Management',
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
