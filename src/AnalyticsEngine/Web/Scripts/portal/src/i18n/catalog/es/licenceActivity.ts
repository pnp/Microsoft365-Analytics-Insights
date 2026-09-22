import type { licenceActivity as en } from '../en/licenceActivity';

/**
 * Spanish (es-ES) text for the Licence activity page and its panels.
 *
 * Typed against the English module, so a key added there without a translation here fails the
 * build rather than reaching a customer as English text inside a Spanish page.
 */
const licenceActivity: Record<keyof typeof en, string> = {
  // Shared vocabulary
  'licenceActivity.common.unknown': 'Desconocido',
  'licenceActivity.common.notMeasured': 'No medido',
  'licenceActivity.common.source': 'Origen:',
  'licenceActivity.common.lastImported': 'Última importación: {date}',
  'licenceActivity.common.dataFrom': 'Datos de {from} \u2013 {to}',
  'licenceActivity.common.measurementsTaken': '{observed} de {expected} mediciones realizadas',
  'licenceActivity.common.peopleCouldNotBeMatched': 'No se pudieron emparejar {count} personas',
  'licenceActivity.common.service': 'Servicio',
  'licenceActivity.common.licence': 'Licencia',
  'licenceActivity.common.peopleAssigned': 'Personas asignadas',
  'licenceActivity.common.people': 'Personas',
  'licenceActivity.common.department': 'Departamento',
  'licenceActivity.common.country': 'País',
  'licenceActivity.common.average': 'Media',
  'licenceActivity.common.lastActive': 'Última actividad',
  'licenceActivity.common.activity': 'Actividad',
  'licenceActivity.common.data': 'Datos',
  'licenceActivity.common.apply': 'Aplicar',
  'licenceActivity.common.refresh': 'Actualizar',
  'licenceActivity.common.search': 'Buscar',
  'licenceActivity.common.previous': 'Anterior',
  'licenceActivity.common.next': 'Siguiente',

  // Activity bands and coverage statuses
  'licenceActivity.band.high': 'Alta',
  'licenceActivity.band.moderate': 'Moderada',
  'licenceActivity.band.low': 'Baja',
  'licenceActivity.band.zero': 'Sin actividad',
  'licenceActivity.band.unknown': 'Desconocida',
  'licenceActivity.band.description.high': 'Activo en tres cuartas partes o más de las semanas medidas.',
  'licenceActivity.band.description.moderate': 'Activo entre una cuarta parte y menos de tres cuartas partes de las semanas medidas.',
  'licenceActivity.band.description.low': 'Activo en menos de una cuarta parte de las semanas medidas, pero activo al menos en una.',
  'licenceActivity.band.description.zero': 'Los datos de informe completos no muestran actividad en ninguna semana para este usuario y periodo. Todas las semanas se midieron por completo.',
  'licenceActivity.band.description.unknown':
    'Al menos una semana no se pudo medir por completo, por lo que no hay suficientes datos de informe para determinar la actividad de este usuario y periodo. Es posible que falten informes o filas de usuario, que la cobertura sea incompleta o que los contadores de uso no estén disponibles. Esto no es evidencia de ausencia de actividad.',
  'licenceActivity.band.copilotCoverageNote':
    'El informe oficial de uso de Copilot solo cubre a usuarios con licencia de Copilot. Estos gráficos también pueden incluir personas con otras licencias, por lo que una persona sin licencia de Copilot puede aparecer como Desconocida en lugar de inactiva. El estado Desconocido por sí solo no indica si una persona tiene licencia de Copilot.',
  'licenceActivity.band.method':
    'Los niveles de actividad describen en cuántas semanas del periodo estuvo activa una persona: Alta = tres cuartas partes o más, Moderada = entre una cuarta parte y menos de tres cuartas partes, Baja = menos de una cuarta parte, Sin actividad = ninguna. Una semana solo cuenta cuando se importaron todos sus días; si una semana no se pudo medir por completo, el nivel es Desconocido, no cero.',
  'licenceActivity.status.available.label': 'Disponible',
  'licenceActivity.status.available.explanation': 'Medido durante todo el periodo.',
  'licenceActivity.status.partial.label': 'Parcial',
  'licenceActivity.status.partial.explanation': 'Parte de este periodo no se pudo medir por completo, por lo que la actividad aquí puede estar infravalorada.',
  'licenceActivity.status.missingCoverage.label': 'Falta cobertura',
  'licenceActivity.status.missingCoverage.explanation':
    'Parte del periodo elegido no tiene medición detrás, por lo que no se puede mostrar a nadie como inactivo para este servicio.',
  'licenceActivity.status.unmatchableIdentity.label': 'No se pudieron emparejar las identidades',
  'licenceActivity.status.unmatchableIdentity.explanation':
    'Microsoft ocultó las identidades en este informe, por lo que su actividad no se puede vincular a las personas que tienen la licencia.',
  'licenceActivity.status.notImported.label': 'No importado',
  'licenceActivity.status.notImported.explanation': 'Los datos de uso de este servicio nunca se han recopilado en esta implementación.',
  'licenceActivity.status.disabled.label': 'Importación desactivada',
  'licenceActivity.status.disabled.explanation': 'La recopilación de este servicio está desactivada en el instalador.',
  'licenceActivity.status.unknown.label': 'Desconocido',
  'licenceActivity.status.unknown.explanation': 'No medido para esta persona, lo que no equivale a medido como sin actividad.',

  // Source and sampling labels
  'licenceActivity.source.microsoftGraphUsageReport': 'Informes de uso de Microsoft 365',
  'licenceActivity.source.microsoftGraphCopilotUsageReport': 'Informe de uso de Microsoft 365 Copilot',
  'licenceActivity.source.copilotAudit': 'Registro de auditoría de Copilot',
  'licenceActivity.source.copilotInteractions': 'Historial de chats de Copilot',
  'licenceActivity.granularity.weeklySupportingSnapshot': 'una lectura por semana',
  'licenceActivity.granularity.singleRollingWindow': 'un único informe móvil',
  'licenceActivity.granularity.weeklySampleOfRolling7DayReport': 'un informe de 7 días leído por semana',
  'licenceActivity.granularity.eventPositiveOnly': 'solo actividad registrada',
  'licenceActivity.granularity.unknown': 'no aplicable',

  // ActivityCoverageHelp
  'licenceActivity.activityCoverage.summary':
    'Desconocido significa datos insuficientes, no ausencia de actividad. Sin actividad significa que los datos de informe completos no muestran uso.',
  'licenceActivity.activityCoverage.whyUnknown': '¿Por qué la actividad es Desconocida?',
  'licenceActivity.activityCoverage.recordedEventsOnly':
    'Cuando el origen contiene solo eventos registrados, no encontrar eventos no demuestra que la persona estuviera inactiva.',
  'licenceActivity.activityCoverage.showSourcesHelp':
    'En De dónde proceden estas cifras, seleccione Mostrar orígenes de datos para ver el origen, las fechas de informe y el estado de importación de cada servicio.',

  // Coverage and data sources
  'licenceActivity.coverage.title': 'De dónde proceden estas cifras',
  'licenceActivity.coverage.preparedHeld': 'Preparado {prepared} ({age}); conservado hasta {expires}',
  'licenceActivity.coverage.noSourceInfo': 'No se notificó información de origen para estas cifras.',
  'licenceActivity.coverage.mostRecentDataDaysOld': 'los datos más recientes tienen {days} días',
  'licenceActivity.coverage.coversDays': 'cubre {days} días',
  'licenceActivity.dataSource.servicesMeasuredAria': '{available} de {total} servicios medidos por completo',
  'licenceActivity.dataSource.prepared': '\u00b7 preparado {age}',
  'licenceActivity.dataSource.hide': 'Ocultar orígenes de datos',
  'licenceActivity.dataSource.show': 'Mostrar orígenes de datos',

  // Date range
  'licenceActivity.dateRange.preset.lastSettledWeek': 'Última semana asentada',
  'licenceActivity.dateRange.preset.last4FullySettledWeeks': 'Últimas 4 semanas completamente asentadas',
  'licenceActivity.dateRange.preset.last90SettledDays': 'Últimos 90 días asentados',
  'licenceActivity.dateRange.preset.last180SettledDays': 'Últimos 180 días asentados',
  'licenceActivity.dateRange.customRange': 'Intervalo personalizado',
  'licenceActivity.dateRange.daysEnding': '{days} días hasta {date}',
  'licenceActivity.dateRange.from': 'Desde',
  'licenceActivity.dateRange.startDate': 'Fecha de inicio',
  'licenceActivity.dateRange.to': 'Hasta',
  'licenceActivity.dateRange.endDate': 'Fecha de finalización',
  'licenceActivity.dateRange.error.enterBothDates': 'Especifique una fecha de inicio y una fecha de finalización.',
  'licenceActivity.dateRange.error.earliestSupportedDate': 'La fecha mínima admitida es {date}.',
  'licenceActivity.dateRange.error.startOnOrBeforeEnd': 'La fecha de inicio debe ser igual o anterior a la fecha de finalización.',
  'licenceActivity.dateRange.error.endBeforeToday':
    'La fecha de finalización debe ser anterior a hoy (los informes cubren días pasados completos).',
  'licenceActivity.dateRange.error.rangeAtLeastDays': 'El intervalo debe ser de al menos {days} días.',
  'licenceActivity.dateRange.error.rangeNoLongerThanDays': 'El intervalo no puede ser de más de {days} días.',

  // Overview and selected licence
  'licenceActivity.overview.peopleWithLicence': 'Personas con licencia',
  'licenceActivity.overview.countingEachPersonOnce': 'en esta selección, contando cada persona una vez',
  'licenceActivity.overview.licenceTypes': 'Tipos de licencia',
  'licenceActivity.overview.assignedOne': 'asignada en esta selección',
  'licenceActivity.overview.assignedMany': 'asignadas, cada una medida por separado',
  'licenceActivity.overview.selectedLicence': 'Licencia seleccionada',
  'licenceActivity.overview.peopleHoldItSeeTabs': '{count} personas la tienen \u00b7 consulte Por servicio y Personas',
  'licenceActivity.overview.noneChosen': 'Ninguna elegida',
  'licenceActivity.overview.chooseLicenceBelow': 'Elija una licencia abajo para ver sus servicios y personas',
  'licenceActivity.selectedLicence.aria': 'Licencia seleccionada',
  'licenceActivity.selectedLicence.choose': 'Elegir una licencia',
  'licenceActivity.selectedLicence.peopleHold': '{count} personas tienen esta licencia',

  // Assignments and demographic breakdowns
  'licenceActivity.assignments.empty': 'No se encontraron asignaciones de licencias para esta selección.',
  'licenceActivity.assignments.title': 'Asignaciones de licencias',
  'licenceActivity.assignments.description': 'Seleccione una licencia para ver cuánto se usa cada servicio y quién la tiene.',
  'licenceActivity.assignments.showingOf': 'Mostrando {shown} de {total}.',
  'licenceActivity.assignments.filterPlaceholder': 'Filtrar por nombre o código de licencia',
  'licenceActivity.assignments.filterAria': 'Filtrar licencias',
  'licenceActivity.assignments.noMatches': 'Ninguna licencia coincide con “{filter}”.',
  'licenceActivity.demographics.description':
    'Personas con alguna licencia importada, no necesariamente una licencia para todos los servicios, por {segment}, de mayor a menor.',
  'licenceActivity.demographics.capped':
    'Se muestran solo los {count} grupos más grandes por número de personas asignadas; esta no es la lista completa.',

  // Workload distributions
  'licenceActivity.distribution.activeOfMeasured': '{active} de {measured} activos ({rate})',
  'licenceActivity.distribution.aria': 'Distribución de actividad de {label}',
  'licenceActivity.distribution.noActivity': 'No hay actividad disponible para esta licencia.',
  'licenceActivity.distribution.activeCountExplanation':
    'El recuento "activo" incluye a todas las personas con cualquier actividad medida, entre las personas cuyo periodo completo se pudo medir.',
  'licenceActivity.distribution.bandTitle': '{label}: {count}. {description}',
  'licenceActivity.distribution.bandLegendLabel': '{label} {count}',

  // Users drill-down and table
  'licenceActivity.users.sort.mostActive': 'Más activos primero',
  'licenceActivity.users.sort.leastActive': 'Menos activos primero',
  'licenceActivity.users.sort.mostRecentlyActive': 'Actividad más reciente',
  'licenceActivity.users.sort.longestSinceActive': 'Más tiempo desde la actividad',
  'licenceActivity.users.sort.upnAsc': 'Dirección de inicio de sesión (A-Z)',
  'licenceActivity.users.sort.upnDesc': 'Dirección de inicio de sesión (Z-A)',
  'licenceActivity.users.topCountAria': 'Número de personas en cada lista',
  'licenceActivity.users.holdLicenceChooseService':
    '{count} personas tienen esta licencia. Elija un servicio para clasificarlas por cuánto lo usan.',
  'licenceActivity.users.showTop': 'Mostrar los primeros',
  'licenceActivity.users.ofEach': 'de cada uno',
  'licenceActivity.users.refreshAria': 'Actualizar la lista',
  'licenceActivity.users.clearSearch': 'Borrar la búsqueda e intentarlo de nuevo',
  'licenceActivity.users.incompleteWarning':
    'Las personas con actividad registrada siguen apareciendo como más activas; nadie se muestra como menos activo para este servicio.',
  'licenceActivity.users.loading': 'Cargando personas...',
  'licenceActivity.users.mostActive': 'Más activos',
  'licenceActivity.users.topN': 'primeros {count}',
  'licenceActivity.users.leastActive': 'Menos activos',
  'licenceActivity.users.bottomN': 'últimos {count}',
  'licenceActivity.users.everyoneWithLicence': 'Todos con esta licencia',
  'licenceActivity.users.peopleCount': '{count} personas',
  'licenceActivity.users.noStaffNames':
    'Este producto no recopila los nombres del personal, por lo que las personas se muestran y se buscan por su dirección de inicio de sesión.',
  'licenceActivity.users.searchPlaceholder': 'Buscar por dirección de inicio de sesión',
  'licenceActivity.users.searchAria': 'Buscar usuarios',
  'licenceActivity.users.sortAria': 'Ordenar usuarios',
  'licenceActivity.users.showingRange': 'Mostrando {from}\u2013{to} de {total} personas',
  'licenceActivity.users.pageOf': 'Página {page} de {totalPages}',
  'licenceActivity.users.rankUnavailableIncomplete':
    'La actividad de {workload} no está medida por completo, por lo que no se puede clasificar a las personas aquí; consulte la nota anterior.',
  'licenceActivity.users.rankUnavailableEmpty': 'No se puede clasificar a nadie para este servicio.',
  'licenceActivity.users.noSearchMatches': 'Nadie coincide con la búsqueda.',
  'licenceActivity.users.nobodyToShow': 'No hay nadie que mostrar para esta selección.',
  'licenceActivity.users.couldNotLoad': 'No se pudieron cargar las personas que tienen esta licencia.',
  'licenceActivity.users.expandAria': 'Expandir',
  'licenceActivity.users.everyServiceFor': 'Todos los servicios de {user}',
  'licenceActivity.users.whereItComesFrom': 'De dónde procede',
  'licenceActivity.users.activeMeasuredExpected': 'Activo / medido (esperado)',
  'licenceActivity.users.person': 'Persona',
  'licenceActivity.users.averageActions': 'Acciones medias',
  'licenceActivity.users.activeMeasured': 'Activo / medido',
  'licenceActivity.users.showAllServicesFor': 'Mostrar todos los servicios de {user}',
  'licenceActivity.users.accountDisabled': 'Cuenta deshabilitada',
  'licenceActivity.users.samplesMeasured': '{observed} de {expected} medidos',
  'licenceActivity.users.activeObservedOfExpected': '{base} de {expected}',

  // Page
  'licenceActivity.page.title': 'Actividad de licencias',
  'licenceActivity.page.preview': 'Versión preliminar',
  'licenceActivity.page.intro':
    'Qué licencias están asignadas y cuánto usan realmente las personas que las tienen cada servicio de Microsoft 365. Cada servicio se muestra por separado y nunca se combina en una sola puntuación. Los datos de informe que faltan o están incompletos se muestran como "Desconocido", no como prueba de ausencia de actividad.',
  'licenceActivity.page.previewNote':
    'Este informe está en versión preliminar. Las cifras se conservan hasta 5 minutos antes de volver a calcularse, por lo que una importación muy reciente puede no aparecer inmediatamente y la primera vista de un intervalo de fechas nuevo tarda más. Nada de esto es un juicio sobre la productividad de nadie ni una recomendación para quitar una licencia: solo es evidencia de actividad.',
  'licenceActivity.page.checkingAvailability': 'Comprobando disponibilidad...',
  'licenceActivity.page.availabilityFailed': 'No se pudo comprobar la disponibilidad de la actividad de licencias.',
  'licenceActivity.page.reload': 'Volver a cargar',
  'licenceActivity.page.notAvailable': 'Los informes de actividad de licencias no están disponibles en esta implementación.',
  'licenceActivity.page.reportingWindow': 'Ventana de informe',
  'licenceActivity.page.filterByDepartment': 'Filtrar por departamento',
  'licenceActivity.page.allDepartments': 'Todos los departamentos',
  'licenceActivity.page.recentOptionsHidden': 'Mostrando hasta {count} opciones recientes; algunas pueden estar ocultas.',
  'licenceActivity.page.filterByCountry': 'Filtrar por país',
  'licenceActivity.page.allCountries': 'Todos los países',
  'licenceActivity.page.exportSummaryAndPeopleTooltip':
    'Una copia de Excel del resumen más las personas exactas que aparecen actualmente en la pestaña Personas. Se genera a partir de las cifras ya visibles en pantalla, por lo que coincide con lo que puede ver.',
  'licenceActivity.page.exportSummaryTooltip':
    'Una copia de Excel del resumen de licencias y servicios (solo totales). Se genera a partir de las cifras ya visibles en pantalla.',
  'licenceActivity.page.exportUnavailableTooltip': 'Disponible cuando se haya cargado el informe.',
  'licenceActivity.page.exporting': 'Exportando...',
  'licenceActivity.page.exportToExcel': 'Exportar a Excel',
  'licenceActivity.page.exportFailed': 'No se pudo exportar el libro.',
  'licenceActivity.page.tryAgain': 'Intentarlo de nuevo',
  'licenceActivity.page.loading': 'Cargando actividad de licencias...',
  'licenceActivity.page.overviewFailed': 'No se pudo cargar la información general de actividad de licencias.',
  'licenceActivity.page.tabOverview': 'Información general',
  'licenceActivity.page.tabByService': 'Por servicio',
  'licenceActivity.page.tabByDemographic': 'Por departamento y país',
  'licenceActivity.page.tabPeople': 'Personas',
  'licenceActivity.page.activityByService': 'Actividad por servicio',
  'licenceActivity.page.activityByServiceSubtitle':
    'Cada servicio de Microsoft 365 por separado, nunca combinado en una sola puntuación',
  'licenceActivity.page.chooseLicenceOverview':
    'Elija una licencia en la pestaña Información general para ver cuánto se usa cada servicio.',
  'licenceActivity.page.activityByDemographic': 'Actividad por departamento y país',
  'licenceActivity.page.activityByDemographicSubtitle':
    'Dónde están las licencias en la organización y cuánto se están usando',
  'licenceActivity.page.demographicCapped':
    'Las listas de departamentos y países están limitadas, por lo que puede que no los muestren todos.',
  'licenceActivity.page.byDepartment': 'Por departamento',
  'licenceActivity.page.byCountry': 'Por país',
  'licenceActivity.page.noDemographicBreakdown':
    'No hay desglose por departamento o país disponible para esta selección.',
  'licenceActivity.page.peopleHoldingLicence': 'Personas con esta licencia',
  'licenceActivity.page.peopleSubtitle': 'Quién usa y quién no usa una licencia',
  'licenceActivity.page.selectLicenceForPeople':
    'Seleccione una licencia en la pestaña Información general para ver quién tiene más y menos actividad, o para explorar todos los usuarios que la tienen.',
};

export default licenceActivity;
