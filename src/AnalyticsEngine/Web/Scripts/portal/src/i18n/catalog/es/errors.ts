import type { errors as en } from '../en/errors';

/**
 * Spanish (es-ES) text for the messages the API layer raises and the pages put on screen.
 *
 * Typed against the English module, so a key added there without a translation here fails the
 * build rather than reaching a customer as English text inside a Spanish page.
 */
const errors: Record<keyof typeof en, string> = {
  // Shared API/session errors
  'errors.http.sessionExpired': 'La sesión ha caducado. Vuelva a cargar la página para iniciar sesión de nuevo.',
  'errors.userLookup.requestFailed': 'La solicitud ha fallado ({status})',

  // Agent costs API
  'errors.agentCosts.availabilityFailed': 'No se ha podido cargar la disponibilidad de costes de agentes ({status}).',
  'errors.agentCosts.summaryFailed': 'No se ha podido cargar el resumen de costes de agentes ({status}).',
  'errors.agentCosts.trendFailed': 'No se ha podido cargar la tendencia diaria de créditos ({status}).',
  'errors.agentCosts.breakdownFailed': 'No se ha podido cargar el desglose de créditos ({status}).',
  'errors.agentCosts.detailFailed': 'No se han podido cargar las filas detalladas de créditos ({status}).',
  'errors.agentCosts.azureBreakdownFailed': 'No se ha podido cargar el desglose de costes de Azure ({status}).',
  'errors.agentCosts.topUsersFailed': 'No se ha podido cargar el consumo de créditos por usuario ({status}).',
  'errors.agentCosts.filtersFailed': 'No se han podido cargar los filtros disponibles ({status}).',

  // Copilot adoption API
  'errors.copilotAdoption.analysisStillRunning': 'El análisis de adopción de Copilot está tardando más de lo esperado y aún no ha finalizado. Sigue ejecutándose en el servidor; vuelva a cargar la página dentro de unos minutos.',
  'errors.copilotAdoption.availabilityFailed': 'No se ha podido cargar la disponibilidad de adopción de Copilot ({status}).',
  'errors.copilotAdoption.summaryFailed': 'No se ha podido cargar el resumen de adopción de Copilot ({status}).',
  'errors.copilotAdoption.filtersFailed': 'No se han podido cargar los filtros de adopción de Copilot ({status}).',
  'errors.copilotAdoption.licensedUsersFailed': 'No se han podido cargar los usuarios con licencia de Copilot ({status}).',
  'errors.copilotAdoption.opportunitiesFailed': 'No se han podido cargar las oportunidades de licencias de Copilot ({status}).',
  'errors.copilotAdoption.coworkFailed': 'No se ha podido cargar la lista de preparación de Cowork ({status}).',
  'errors.copilotAdoption.queriesFailed': 'No se han podido cargar las consultas de adopción de Copilot ({status}).',

  // DLP API
  'errors.dlp.availabilityFailed': 'No se ha podido cargar la disponibilidad de DLP ({status}).',
  'errors.dlp.summaryFailed': 'No se ha podido cargar el resumen de DLP ({status}).',

  // Health API
  'errors.health.summaryFailed': 'No se ha podido cargar el estado del sistema ({status}).',
  'errors.health.dataFailed': 'No se ha podido cargar la información general de datos ({status}).',
  'errors.health.livenessFailed': 'No se ha podido cargar el estado de actividad de la importación ({status}).',
  'errors.health.exceptionsFailed': 'No se han podido cargar las excepciones ({status}).',
  'errors.health.componentsFailed': 'No se ha podido cargar el estado de los componentes ({status}).',
  'errors.health.configFailed': 'No se ha podido cargar la configuración ({status}).',

  // Install/profiling/status APIs
  'errors.installLog.loadFailed': 'No se ha podido cargar el registro de instalación ({status}).',
  'errors.profiling.statusFailed': 'No se ha podido cargar el estado de generación de perfiles ({status}).',
  'errors.profiling.traceLogsFailed': 'No se han podido cargar los registros de seguimiento de generación de perfiles ({status}).',
  'errors.systemStatus.loadFailed': 'No se ha podido cargar el estado del sistema ({status}).',
  'errors.updateCheck.failed': 'No se han podido buscar actualizaciones ({status}).',

  // Licence activity API
  'errors.licenceActivity.figuresExpired': 'Estas cifras ya no se conservan. Actualice el informe para recuperar un conjunto actualizado.',
  'errors.licenceActivity.userDetailsImportOff': 'La actividad de licencias no está disponible: la importación de detalles de usuario está desactivada en esta implementación.',
  'errors.licenceActivity.badRequest': 'Se ha rechazado esa solicitud. Compruebe las fechas y los filtros seleccionados.',
  'errors.licenceActivity.availabilityBusy': 'El servidor está ocupado o no ha podido preparar la disponibilidad de actividad de licencias. Inténtelo de nuevo en unos instantes.',
  'errors.licenceActivity.availabilityForbidden': 'No tiene permiso para ver la disponibilidad de actividad de licencias.',
  'errors.licenceActivity.availabilityFailed': 'No se ha podido cargar la disponibilidad de actividad de licencias ({status}).',
  'errors.licenceActivity.overviewBusy': 'El servidor está ocupado o no ha podido preparar el resumen de actividad de licencias. Inténtelo de nuevo en unos instantes.',
  'errors.licenceActivity.overviewForbidden': 'No tiene permiso para ver el resumen de actividad de licencias.',
  'errors.licenceActivity.overviewFailed': 'No se ha podido cargar el resumen de actividad de licencias ({status}).',
  'errors.licenceActivity.usersBusy': 'El servidor está ocupado o no ha podido preparar los usuarios con licencia. Inténtelo de nuevo en unos instantes.',
  'errors.licenceActivity.usersForbidden': 'No tiene permiso para ver los usuarios con licencia.',
  'errors.licenceActivity.usersFailed': 'No se han podido cargar los usuarios con licencia ({status}).',
  'errors.licenceActivity.excelExportBusy': 'El servidor está ocupado o no ha podido preparar la exportación de Excel. Inténtelo de nuevo en unos instantes.',
  'errors.licenceActivity.excelExportForbidden': 'No tiene permiso para ver la exportación de Excel.',
  'errors.licenceActivity.excelExportFailed': 'No se ha podido cargar la exportación de Excel ({status}).',

  // Reports API
  'errors.reports.areasFailed': 'No se han podido cargar las áreas de informes ({status}).',
  'errors.reports.copilotReportFailed': 'No se ha podido cargar el informe de Copilot ({status}).',
  'errors.reports.copilotAgentsReportFailed': 'No se ha podido cargar el informe de agentes de Copilot ({status}).',
  'errors.reports.usageReportFailed': 'No se ha podido cargar el informe de uso ({status}).',
  'errors.reports.officeAppsReportFailed': 'No se ha podido cargar el informe de aplicaciones de Office ({status}).',
  'errors.reports.spoAuditReportFailed': 'No se ha podido cargar el informe de auditoría de SharePoint ({status}).',
  'errors.reports.webTrafficReportFailed': 'No se ha podido cargar el informe de tráfico web ({status}).',
  'errors.reports.callsReportFailed': 'No se ha podido cargar el informe de llamadas ({status}).',
  'errors.reports.emailsReportFailed': 'No se ha podido cargar el informe de correos electrónicos ({status}).',

  // Teams Explorer API
  'errors.teamsExplorer.dataSourcesFailed': 'No se han podido cargar los orígenes de datos de Teams ({status}).',
  'errors.teamsExplorer.overviewFailed': 'No se ha podido cargar el resumen de Teams ({status}).',
  'errors.teamsExplorer.adoptionFailed': 'No se ha podido cargar la adopción de Teams ({status}).',
  'errors.teamsExplorer.meetingsFailed': 'No se han podido cargar las reuniones y llamadas de Teams ({status}).',
  'errors.teamsExplorer.collaborationFailed': 'No se han podido cargar los equipos y canales ({status}).',
  'errors.teamsExplorer.conversationsFailed': 'No se ha podido cargar la información de conversaciones ({status}).',
  'errors.teamsExplorer.peopleFailed': 'No se han podido cargar las personas de Teams ({status}).',
  'errors.teamsExplorer.exportPeopleFailed': 'No se han podido exportar las personas ({status}).',
  'errors.teamsExplorer.exportDormantFailed': 'No se han podido exportar los inactivos ({status}).',
  'errors.teamsExplorer.exportTeamsFailed': 'No se han podido exportar los equipos ({status}).',
  'errors.teamsExplorer.exportChannelsFailed': 'No se han podido exportar los canales ({status}).',
  'errors.teamsExplorer.exportAdoptionFailed': 'No se ha podido exportar la adopción ({status}).',

  // Web activity API
  'errors.webActivity.dataSourcesFailed': 'No se han podido cargar los orígenes de datos de tráfico web ({status}).',
  'errors.webActivity.overviewFailed': 'No se ha podido cargar el resumen de actividad web ({status}).',
  'errors.webActivity.visitsFailed': 'No se han podido cargar las visitas ({status}).',
  'errors.webActivity.pagesFailed': 'No se han podido cargar las vistas de página ({status}).',
  'errors.webActivity.journeysFailed': 'No se han podido cargar los recorridos de visitantes ({status}).',
  'errors.webActivity.geographyFailed': 'No se ha podido cargar la geografía ({status}).',
  'errors.webActivity.searchFailed': 'No se han podido cargar las búsquedas web ({status}).',
  'errors.webActivity.technologyFailed': 'No se ha podido cargar la tecnología ({status}).',
  'errors.webActivity.exportPagesFailed': 'No se han podido exportar las páginas ({status}).',
  'errors.webActivity.exportQuietPagesFailed': 'No se han podido exportar las páginas silenciosas ({status}).',
  'errors.webActivity.exportSlowPagesFailed': 'No se han podido exportar las páginas lentas ({status}).',
  'errors.webActivity.exportEntryPagesFailed': 'No se han podido exportar las páginas de entrada ({status}).',
  'errors.webActivity.exportExitPagesFailed': 'No se han podido exportar las páginas de salida ({status}).',
  'errors.webActivity.exportTransitionsFailed': 'No se han podido exportar las transiciones ({status}).',
  'errors.webActivity.exportFlowsFailed': 'No se han podido exportar los flujos ({status}).',
  'errors.webActivity.exportSearchTermsFailed': 'No se han podido exportar los términos de búsqueda ({status}).',
  'errors.webActivity.exportTechnologyFailed': 'No se ha podido exportar la tecnología ({status}).',
};

export default errors;
