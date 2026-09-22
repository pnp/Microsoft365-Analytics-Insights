import type { health as en } from '../en/health';

/**
 * Spanish (es-ES) text for the Service health page and its panels.
 *
 * Typed against the English module, so a key added there without a translation here fails the
 * build rather than reaching a customer as English text inside a Spanish page.
 */
const health: Record<keyof typeof en, string> = {
  // Shared health UI
  'health.action.refresh': 'Actualizar',
  'health.action.refreshing': 'Actualizando...',
  'health.section.loaded': 'cargado {when}',
  'health.section.loading': 'Cargando {title}...',
  'health.status.checking': 'Comprobando...',
  'health.status.degraded': 'Degradado',
  'health.status.healthy': 'Correcto',
  'health.status.unknown': 'Desconocido',
  'health.status.unhealthy': 'Erróneo',
  'health.time.daysAgo': 'hace {days} días',
  'health.time.hoursAgo': 'hace {hours} horas',
  'health.time.justNow': 'ahora mismo',
  'health.time.minutesAgo': 'hace {minutes} min',
  'health.time.never': 'nunca',

  // Health page
  'health.page.title': 'Estado del sistema{buildLabel}',
  'health.page.description': 'Seleccione una subsección abajo: cada una carga sus propios datos bajo demanda y se actualiza automáticamente cada {seconds}s mientras está abierta (en caché en el servidor). Esto complementa las reglas de alerta de Azure Monitor / Application Insights (que notifican cuando algo falla): es el panel verde de un vistazo.',
  'health.page.overviewLoaded': 'Resumen cargado {when}.',
  'health.page.alertsGuidance': 'Para recibir alertas (no solo consultar), configure las reglas de alerta de Azure Monitor / Application Insights en la guía wiki Health Alerts. Los mismos eventos personalizados que se muestran aquí respaldan esas alertas.',
  'health.tabs.componentHealth': 'Estado de componentes',
  'health.tabs.dataOverview': 'Resumen de datos',
  'health.tabs.exceptions': 'Excepciones',
  'health.tabs.importLiveness': 'Actividad de importación',
  'health.tabs.overview': 'Resumen',

  // Overview panel
  'health.overview.loadingSystemHealth': 'Cargando estado del sistema...',
  'health.overview.intro': 'Una vista única de "¿funciona?". Todos los valores son de solo lectura y de mejor esfuerzo: una incidencia de un origen de datos atenúa una subsección, pero nunca rompe la página. Este Resumen acumula todas las secciones, pero omite los análisis pesados de la base de datos (solo se cargan al abrir la pestaña Datos), por lo que sigue siendo ligero incluso en un inquilino grande.',
  'health.overview.appInsightsNotConfigured': 'Application Insights no está configurado para esta aplicación web, por lo que las subsecciones Actividad de importación, Excepciones y Estado de componentes (Application Insights) no están disponibles. El resumen de datos, la configuración y las comprobaciones de credencial en tiempo de ejecución / Service Bus siguen funcionando.',
  'health.overview.subSectionsHeading': 'Subsecciones',
  'health.overview.sectionStatusAriaLabel': 'Estado de sección',
  'health.overview.columnSubSection': 'Subsección',
  'health.overview.columnStatus': 'Estado',
  'health.overview.columnNotes': 'Notas',
  'health.overview.open': 'Abrir',

  // Import liveness panel
  'health.liveness.title': 'Actividad de importación',
  'health.liveness.description': '¿Cada importador sigue iterando y finalizando? Un ciclo completo de importación de actividad debería completarse al menos una vez cada {hours} horas. "Último ciclo confirmado" es el evento FinishedImportCycle; las filas por sección son los eventos FinishedSectionImport.',
  'health.liveness.appInsightsNotConfigured': 'Application Insights no está configurado, por lo que la actividad de importación no está disponible.',
  'health.liveness.loadError': 'No se pudo cargar la actividad de importación: {error}',
  'health.liveness.lastCycleHeading': 'Último ciclo confirmado por trabajo',
  'health.liveness.lastCycleAriaLabel': 'Último ciclo por trabajo',
  'health.liveness.columnImporter': 'Importador',
  'health.liveness.columnLastCycleUtc': 'Último ciclo (UTC)',
  'health.liveness.columnFreshness': 'Frescura',
  'health.liveness.columnDuration': 'Duración',
  'health.liveness.noFinishedImportCycles': 'Todavía no hay eventos FinishedImportCycle en la ventana de retención.',
  'health.liveness.webTrackerHeading': 'Rastreador web (pageViews en App Insights, últimas 24 h)',
  'health.liveness.pageViewsBadge': '{count} vistas de página',
  'health.liveness.lastSeen': 'visto por última vez {when}',
  'health.liveness.noPageViews': 'ninguno: es posible que el rastreador web no esté implementado en el sitio o que no envíe datos a App Insights',
  'health.liveness.lastRunHeading': 'Última ejecución por sección',
  'health.liveness.lastSectionAriaLabel': 'Últimas importaciones de sección',
  'health.liveness.columnSection': 'Sección',
  'health.liveness.columnLastRunUtc': 'Última ejecución (UTC)',
  'health.liveness.noFinishedSectionImports': 'Todavía no hay eventos FinishedSectionImport en la ventana de retención.',
  'health.liveness.heartbeatsHeading': 'Latidos de importadores',
  'health.liveness.heartbeatsAriaLabel': 'Latidos de importadores',
  'health.liveness.columnJob': 'Trabajo',
  'health.liveness.columnLastBeatUtc': 'Último latido (UTC)',
  'health.liveness.columnLastCycleSecs': 'Seg. del último ciclo',
  'health.liveness.noHeartbeats': 'Los eventos ImporterHeartbeat de temporizador independiente todavía no se emiten (ese host pertenece a una fase posterior). Hasta entonces, la señal de actividad es "Último ciclo confirmado" anterior; tenga en cuenta que solo se emite cuando se completa un ciclo, por lo que un trabajo bloqueado a mitad de ciclo seguiría pareciendo reciente.',

  // Exceptions panel
  'health.exceptions.title': 'Resumen de excepciones (últimas 24 h)',
  'health.exceptions.description': 'Una comprobación general barata: todos los trabajos web registran errores en Application Insights, por lo que un recuento al alza es una advertencia temprana de errores que ninguna comprobación específica anticipa.',
  'health.exceptions.appInsightsNotConfigured': 'Application Insights no está configurado, por lo que el resumen de excepciones no está disponible.',
  'health.exceptions.loadError': 'No se pudieron cargar las excepciones: {error}',
  'health.exceptions.last24hLabel': 'excepciones en las últimas 24 horas',
  'health.exceptions.sqlCapacityWarning': '{count} de estas parecen errores de capacidad / solo lectura de SQL: compruebe el almacenamiento / edición de la base de datos. Normalmente esto significa que se ha dejado de escribir datos.',
  'health.exceptions.perHourHeading': 'Por hora',
  'health.exceptions.topTypesHeading': 'Tipos de excepción principales',
  'health.exceptions.topTypesAriaLabel': 'Tipos de excepción principales',
  'health.exceptions.columnType': 'Tipo',
  'health.exceptions.columnProblemId': 'Id. de problema',
  'health.exceptions.columnCount': 'Recuento',
  'health.exceptions.empty': 'No se han registrado excepciones en las últimas 24 horas.',

  // Component health panel
  'health.components.title': 'Estado de componentes',
  'health.components.description': 'Estado más reciente por componente. Las comprobaciones de la credencial en tiempo de ejecución (expiración) y Service Bus (cola de llamadas de Teams) se ejecutan aquí actualmente; SQL, Activity API, Graph, Key Vault, Redis y DNS se rellenarán cuando llegue el emisor HealthCheck en tiempo de ejecución (una fase posterior).',
  'health.components.loadError': 'No se pudo cargar el estado de componentes: {error}',
  'health.components.ariaLabel': 'Estado de componentes',
  'health.components.columnComponent': 'Componente',
  'health.components.columnStatus': 'Estado',
  'health.components.columnDetail': 'Detalle',
  'health.components.columnDaysToExpiry': 'Días hasta expirar',
  'health.components.columnLastChecked': 'Última comprobación',
  'health.components.empty': 'Todavía no hay estado de componentes disponible.',

  // Data overview panel
  'health.data.title': 'Resumen de datos',
  'health.data.description': 'Volumen y frescura desde la base de datos. Los recuentos de filas son aproximados (se leen de metadatos de índices, por lo que un inquilino grande no recibe un COUNT(*) en cada carga); las columnas de últimas 24 h / 7 d muestran lo que realmente está fluyendo.',
  'health.data.recentVolumeWarning': 'El análisis de volumen y frescura de 24 h/7 d no terminó a tiempo en esta base de datos, por lo que esas columnas muestran "-". Los totales aproximados siguen cargándose. (Esto es esperado en inquilinos muy grandes: las columnas de marca de tiempo no están indexadas).',
  'health.data.ariaLabel': 'Resumen de datos',
  'health.data.columnWorkload': 'Carga de trabajo',
  'health.data.columnRows': 'Filas',
  'health.data.columnRowsApproximate': 'Filas (aprox.)',
  'health.data.columnLast24h': 'Últimas 24 h',
  'health.data.columnLast7d': 'Últimos 7 d',
  'health.data.workloadActivityImports': 'Importaciones de actividad (eventos de auditoría)',
  'health.data.workloadWebHits': 'Visitas web',
  'health.data.workloadCopilotInteractions': 'Interacciones de Copilot',
  'health.data.seeAuditEventFreshness': 'ver frescura de eventos de auditoría',
  'health.data.workloadSentEmails': 'Correos electrónicos enviados',
  'health.data.workloadTeamsCallRecords': 'Registros de llamadas de Teams',
  'health.data.workloadTeamsDiscoveredTracked': 'Teams detectados / seguidos',
  'health.data.workloadUsers': 'Usuarios',
  'health.data.freshnessHeading': 'Frescura',
  'health.data.freshnessAriaLabel': 'Frescura de datos',
  'health.data.newestAuditEvent': 'Evento de auditoría más reciente',
  'health.data.newestHit': 'Visita más reciente',
  'health.data.databaseSize': 'Tamaño de base de datos (archivos de datos)',
};

export default health;
