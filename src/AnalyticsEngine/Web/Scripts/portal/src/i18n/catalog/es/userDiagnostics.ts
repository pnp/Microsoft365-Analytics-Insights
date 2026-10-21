import type en from '../en/userDiagnostics';

/**
 * Spanish (es-ES) text for the per-user licence and data diagnostics on Administración > Búsqueda de usuario.
 *
 * Typed against its English counterpart, so a key added there and forgotten here is a build error
 * rather than an English sentence appearing mid-page for a Spanish reader.
 */
export const userDiagnostics: Record<keyof typeof en, string> = {
  'userDiagnostics.title': 'Diagnóstico de licencias y datos',
  'userDiagnostics.intro':
    'Pruebas de por qué esta persona aparece con o sin licencia y de lo recientes que son los datos que se guardan sobre ella. Nada de esto modifica ningún dato, salvo que solicite una actualización.',
  'userDiagnostics.loading': 'Cargando el diagnóstico...',
  'userDiagnostics.loadFailed': 'No se pudo cargar el diagnóstico.',
  'userDiagnostics.loadedAt': 'Cargado {when}',

  'userDiagnostics.findings.title': 'Qué indican las pruebas',
  'userDiagnostics.findings.none': 'Nada en las pruebas guardadas parece incorrecto para esta persona.',
  'userDiagnostics.finding.noStoredUser':
    'La importación de usuarios nunca ha guardado a esta persona, y un informe no puede contar a alguien que no está en la base de datos. Revise el ámbito de importación más abajo y compare con Microsoft Graph para ver si el directorio la tiene.',
  'userDiagnostics.finding.noStoredLicences':
    'No hay asignaciones de licencia guardadas para esta persona. Es lo que contiene la base de datos, no una prueba de que Entra ID no le asigne nada: compare con Microsoft Graph para comprobarlo.',
  'userDiagnostics.finding.noStoredCopilotSeat':
    'Ninguna de las licencias guardadas cuenta como puesto de Microsoft 365 Copilot con las reglas actuales, así que Adopción de Copilot trata a esta persona como sin licencia.',
  'userDiagnostics.finding.accountDisabled': 'La cuenta está deshabilitada en los datos guardados.',
  'userDiagnostics.finding.scopeExcluded':
    'Esta persona está fuera del ámbito de usuarios configurado, así que la importación no guarda datos sobre ella y la lectura en directo del directorio no está disponible.',
  'userDiagnostics.finding.scopeUnknown':
    'No se pudo resolver por completo el ámbito de usuarios, así que no se sabe si esta persona está incluida. La lectura en directo del directorio no estará disponible hasta que se resuelva; consulte Administración > Ámbito de usuarios.',
  'userDiagnostics.finding.userImportDisabled': 'La importación de metadatos y licencias de usuarios está desactivada, así que las licencias guardadas no se actualizan.',
  'userDiagnostics.finding.userImportNotRecorded':
    'No hay ninguna importación de usuarios completada registrada en Azure Table Storage, así que no se puede mostrar la antigüedad de las licencias guardadas.',
  'userDiagnostics.finding.userImportOverdue':
    'La importación de usuarios se completó por última vez {when}, hace más tiempo del que permite su intervalo configurado. Puede que las licencias guardadas estén desactualizadas: revise el importador en la página Estado.',
  'userDiagnostics.finding.licenceHistoryUnavailable': 'Esta base de datos todavía no registra el historial de licencias, así que no se pueden mostrar las asignaciones anteriores.',
  'userDiagnostics.finding.catalogueMissingSku':
    'La SKU {sku} no figura en el catálogo de licencias de esta versión, así que se guarda con su número de pieza. Una versión más reciente puede darle otro nombre y otra clasificación.',
  'userDiagnostics.finding.possibleCopilotRuleGapNotCounted':
    'El catálogo de licencias de Microsoft incluye los planes de servicio del puesto de Microsoft 365 Copilot en {name} ({sku}), pero las reglas de Copilot no lo cuentan como puesto. Quien tenga Copilot solo a través de esta licencia aparece sin licencia en Adopción de Copilot. Pruebe una selección hipotética de puestos en ese informe para ver el efecto.',
  'userDiagnostics.finding.possibleCopilotRuleGapCounted':
    '{name} ({sku}) cuenta como puesto de Copilot según las reglas, pero el catálogo de licencias de Microsoft no incluye en ella los planes de servicio del puesto de Copilot.',

  'userDiagnostics.scope.title': 'Ámbito de importación',
  'userDiagnostics.scope.unfiltered': 'No hay ningún filtro de ámbito de usuarios configurado, así que todo el mundo está incluido.',
  'userDiagnostics.scope.inScope': 'Incluida: es miembro directo de un grupo al que se resolvió el filtro de ámbito ({members} personas en {groups} grupos, resuelto {when}).',
  'userDiagnostics.scope.excluded': 'Excluida: no es miembro directo de ningún grupo al que se resolvió el filtro de ámbito ({members} personas en {groups} grupos, resuelto {when}).',
  'userDiagnostics.scope.unknown': 'Desconocido: no se pudo resolver el filtro de ámbito de usuarios ({reason}).',
  'userDiagnostics.scope.failure.directoryRead': 'falló una lectura del directorio',
  'userDiagnostics.scope.failure.budgetExhausted': 'se alcanzó el límite de lecturas del directorio',
  'userDiagnostics.scope.failure.clientUnavailable': 'no se pudo crear un cliente de Microsoft Graph',
  'userDiagnostics.scope.failure.unexpected': 'un error inesperado',

  'userDiagnostics.freshness.title': '¿Qué antigüedad tienen las licencias guardadas?',
  'userDiagnostics.freshness.metadataLastWritten': 'Perfil escrito por última vez por la importación',
  'userDiagnostics.freshness.metadataLastWrittenHint':
    'Cuándo modificó la importación por última vez la fila de perfil de esta persona. A una persona sin cambios no se la vuelve a escribir, así que una fecha antigua es normal y no significa que no se hayan comprobado sus licencias.',
  'userDiagnostics.freshness.userImportLastCompleted': 'Última importación de usuarios completada (todo el inquilino)',
  'userDiagnostics.freshness.lastTenantLicenceRefresh': 'Última actualización de licencias registrada (todo el inquilino)',
  'userDiagnostics.freshness.userVerification': 'Última confirmación para esta persona',
  'userDiagnostics.freshness.interval': 'Se ejecuta como máximo cada {hours} horas',
  'userDiagnostics.freshness.notRecorded': 'Sin registrar',
  'userDiagnostics.freshness.importDisabled': 'La importación está desactivada',
  'userDiagnostics.freshness.storageNotConfigured': 'No disponible: no hay Azure Storage configurado',
  'userDiagnostics.freshness.storageUnavailable': 'No disponible: no se pudo leer Azure Table Storage',
  'userDiagnostics.freshness.tenantWideHint':
    'Las horas de todo el inquilino indican que una importación terminó, no que esta persona estuviera incluida. Compare con Microsoft Graph para comprobar ahora a esta persona.',

  'userDiagnostics.licences.title': 'Licencias guardadas',
  'userDiagnostics.licences.none': 'No hay asignaciones de licencia guardadas para esta persona.',
  'userDiagnostics.licences.columnLicence': 'Licencia',
  'userDiagnostics.licences.columnSku': 'Número de pieza de la SKU',
  'userDiagnostics.licences.columnCopilot': 'Cuenta como puesto de Copilot',
  'userDiagnostics.licences.columnWhy': 'Motivo',
  'userDiagnostics.licences.columnHeldSince': 'Asignada desde',
  'userDiagnostics.licences.columnCatalogue': 'Catálogo de licencias',
  'userDiagnostics.licences.heldSince.observed': 'Vista por primera vez {when}',
  'userDiagnostics.licences.heldSince.beforeTrackingBegan': 'Ya asignada cuando empezó el historial ({when})',
  'userDiagnostics.licences.heldSince.unknown': 'Sin registrar',
  'userDiagnostics.licences.catalogue.listed': 'Incluida',
  'userDiagnostics.licences.catalogue.listedWithSeat': 'Incluida, con los planes del puesto de Copilot',
  'userDiagnostics.licences.catalogue.notListed': 'No figura en el catálogo de esta versión',

  'userDiagnostics.reason.seatSkuPrefix': 'La SKU empieza por {rule}, un puesto de Microsoft 365 Copilot o un paquete que lo incluye.',
  'userDiagnostics.reason.excludedSkuPrefix': 'La SKU empieza por {rule}: un producto con la marca Copilot que no es un puesto de Microsoft 365 Copilot.',
  'userDiagnostics.reason.seatDisplayName': 'No se aplica ninguna regla de SKU, pero el nombre del producto corresponde a Microsoft 365 Copilot ({rule}).',
  'userDiagnostics.reason.excludedNameWord': 'El nombre del producto menciona Copilot junto con «{rule}», que identifica otro producto con la marca Copilot.',
  'userDiagnostics.reason.copilotNameWithoutQualifier': 'El nombre del producto menciona Copilot pero no Microsoft 365, así que no se cuenta.',
  'userDiagnostics.reason.noRuleMatched': 'Nada en la SKU ni en su nombre indica Microsoft 365 Copilot.',
  'userDiagnostics.reason.reportOverrideIncluded': 'Se cuenta porque la selección hipotética de puestos del informe la incluye; las reglas no la contarían.',
  'userDiagnostics.reason.reportOverrideExcluded': 'No se cuenta porque la selección hipotética de puestos del informe la excluye; las reglas sí la contarían.',
  'userDiagnostics.reason.unknown': 'No se devolvió ninguna explicación para esta clasificación.',

  'userDiagnostics.history.title': 'Historial de licencias',
  'userDiagnostics.history.hint':
    'Cada fila indica cuándo una actualización de licencias observó que una asignación empezaba o terminaba, no cuándo Entra ID hizo el cambio: el cambio real ocurrió entre la actualización anterior y la que lo observó.',
  'userDiagnostics.history.trackingBegan': 'El historial empezó {when}',
  'userDiagnostics.history.none': 'No hay historial registrado para esta persona.',
  'userDiagnostics.history.columnFrom': 'Observada desde',
  'userDiagnostics.history.columnTo': 'Observada hasta',
  'userDiagnostics.history.open': 'Sigue asignada',
  'userDiagnostics.history.heldBeforeTracking': 'Asignada antes de que empezara el historial',
  'userDiagnostics.history.truncated': 'Se muestran las {count} filas más recientes.',
  'userDiagnostics.history.show': 'Mostrar el historial de licencias',
  'userDiagnostics.history.hide': 'Ocultar el historial de licencias',
  'userDiagnostics.history.recordedByUserRefresh':
    'Observado por una actualización individual: el cambio se produjo en un momento desconocido anterior a esta',

  'userDiagnostics.refresh.title': 'Actualizar a esta persona desde el directorio',
  'userDiagnostics.refresh.description':
    'Pide al importador que vuelva a leer de Microsoft Graph el perfil y las licencias de esta persona y que corrija la base de datos solo para ella. No se modifican los datos de nadie más ni se restablece la importación de todo el inquilino. Los atributos de organización se dejan a la importación programada.',
  'userDiagnostics.refresh.when':
    'El importador recoge la solicitud en su siguiente punto seguro: al comienzo de su siguiente ciclo o en cuanto termine una importación de usuarios que ya esté en curso. Los informes muestran automáticamente cualquier cambio en cuanto se aplica.',
  'userDiagnostics.refresh.button': 'Actualizar desde el directorio...',
  'userDiagnostics.refresh.confirmText':
    '¿Volver a leer a {upn} de Microsoft Graph y actualizar su perfil y sus licencias guardados para que coincidan? Una licencia solo se quita cuando la respuesta del directorio está completa.',
  'userDiagnostics.refresh.confirm': 'Actualizar a esta persona',
  'userDiagnostics.refresh.cancel': 'Cancelar',
  'userDiagnostics.refresh.submitting': 'Enviando la solicitud...',
  'userDiagnostics.refresh.state.queued':
    'Esperando al importador desde {when}. Si ningún importador la recoge antes de {expires}, la solicitud caduca.',
  'userDiagnostics.refresh.state.running': 'El importador está actualizando ahora a esta persona (empezó {when}).',
  'userDiagnostics.refresh.state.expired':
    'La solicitud caducó: ningún importador la recogió en un día. Compruebe en la página Estado que el importador se está ejecutando.',
  'userDiagnostics.refresh.checkAgain': 'Comprobar ahora',
  'userDiagnostics.refresh.observedAt': 'Directorio leído {when}',
  'userDiagnostics.refresh.added': 'Licencias añadidas',
  'userDiagnostics.refresh.removed': 'Licencias quitadas',
  'userDiagnostics.refresh.heldBack': 'Licencias conservadas por falta de pruebas',
  'userDiagnostics.refresh.profileChanges': 'Campos del perfil actualizados',
  'userDiagnostics.refresh.outcome.updated': 'Hecho: la base de datos coincide ahora con el directorio para esta persona.',
  'userDiagnostics.refresh.outcome.unchanged':
    'Hecho: la base de datos ya coincidía con el directorio. No ha cambiado nada y ahora consta que esta persona está confirmada.',
  'userDiagnostics.refresh.outcome.partial':
    'Hecho en parte: se aplicaron los cambios que respaldan las pruebas, pero algunos se retuvieron. Consulte los detalles más abajo.',
  'userDiagnostics.refresh.outcome.scopeExcluded': 'No actualizada: esta persona está fuera del ámbito de usuarios configurado.',
  'userDiagnostics.refresh.outcome.scopeUnknown':
    'No actualizada: no se pudo resolver el ámbito de usuarios, así que no se sabe si esta persona está incluida.',
  'userDiagnostics.refresh.outcome.notStored':
    'No actualizada: esta persona no está en la base de datos. Solo la importación de usuarios programada añade personas.',
  'userDiagnostics.refresh.outcome.userNotFoundInDirectory':
    'No actualizada: Microsoft Graph no tiene ningún usuario con esta identidad. No se ha modificado nada.',
  'userDiagnostics.refresh.outcome.identityMismatch':
    'No actualizada: el directorio devolvió una cuenta distinta de la guardada. No se ha modificado nada.',
  'userDiagnostics.refresh.outcome.permissionDenied':
    'Error: Microsoft Graph rechazó la solicitud. El registro de aplicación necesita el permiso de aplicación User.Read.All. No se ha modificado nada.',
  'userDiagnostics.refresh.outcome.graphUnavailable':
    'Error: no se pudo leer Microsoft Graph. No se ha modificado nada; vuelva a intentarlo dentro de un rato.',
  'userDiagnostics.refresh.outcome.importDisabled': 'No actualizada: la importación de metadatos y licencias de usuarios está desactivada.',
  'userDiagnostics.refresh.outcome.interrupted':
    'Error: el importador se detuvo a medias más de una vez. Revise el importador en la página Estado y vuelva a intentarlo.',
  'userDiagnostics.refresh.outcome.error': 'Error: se produjo un error inesperado. El registro del importador tiene los detalles.',
  'userDiagnostics.refresh.note.incompleteDirectoryAnswer':
    'La respuesta del directorio estaba incompleta, así que no se quitó ninguna licencia basándose en ella.',
  'userDiagnostics.refresh.note.ambiguousLicenceKept':
    'Se conservó una licencia guardada con un nombre de producto anterior, porque esta persona sigue teniendo esa SKU. La importación programada la vuelve a archivar con el nombre actual.',
  'userDiagnostics.refresh.note.userPrincipalNameChanged':
    'El directorio conoce ahora a esta persona con otro nombre de inicio de sesión. El nombre guardado se deja para que lo actualice la importación programada.',
  'userDiagnostics.refresh.note.noLicenceHistory':
    'Esta base de datos no tiene tablas de historial de licencias, así que el cambio no quedó registrado en el historial.',
  'userDiagnostics.refresh.note.reconciledAfterInterruption':
    'Un intento anterior se interrumpió; este volvió a leer el directorio y aplicó lo que seguía siendo distinto.',
  'userDiagnostics.refresh.note.profileNotUpdated':
    'Las licencias se actualizaron, pero el perfil no se pudo actualizar. El registro del importador tiene los detalles.',
  'userDiagnostics.refresh.note.reportCachesNotRefreshed':
    'No se pudo avisar a los informes del cambio de inmediato; lo mostrarán en unos diez minutos, cuando caduquen sus cachés.',
  'userDiagnostics.refresh.unavailable.storageNotConfigured':
    'No disponible: las actualizaciones individuales se ponen en cola en Azure Table Storage y no hay ninguna cuenta de Storage configurada.',
  'userDiagnostics.refresh.unavailable.storageUnavailable': 'No disponible en este momento: no se pudo acceder a Azure Table Storage.',
  'userDiagnostics.refresh.unavailable.importDisabled': 'No disponible: la importación de metadatos y licencias de usuarios está desactivada.',
  'userDiagnostics.refresh.unavailable.notStored':
    'No disponible: esta persona no está en la base de datos. Solo la importación de usuarios programada añade personas; consulte Administración > Importación de usuarios.',
  'userDiagnostics.refresh.unavailable.scopeExcluded': 'No disponible: esta persona está fuera del ámbito de usuarios configurado.',
  'userDiagnostics.refresh.unavailable.scopeUnknown': 'No disponible hasta que se pueda resolver el ámbito de usuarios.',
  'userDiagnostics.refresh.unavailable.queueFull':
    'Ya hay demasiadas actualizaciones en espera. Vuelva a intentarlo cuando el importador las haya procesado.',
  'userDiagnostics.refresh.recent': 'Solicitudes recientes para esta persona',
  'userDiagnostics.refresh.columnRequested': 'Solicitada',
  'userDiagnostics.refresh.columnBy': 'Por',
  'userDiagnostics.refresh.columnResult': 'Resultado',
  'userDiagnostics.refresh.status.queued': 'En espera',
  'userDiagnostics.refresh.status.running': 'En curso',
  'userDiagnostics.refresh.status.expired': 'Caducada',
  'userDiagnostics.refresh.status.updated': 'Actualizada',
  'userDiagnostics.refresh.status.unchanged': 'Ya era correcta',
  'userDiagnostics.refresh.status.partial': 'Aplicada en parte',
  'userDiagnostics.refresh.status.notRefreshed': 'No actualizada',
  'userDiagnostics.refresh.status.failed': 'Error',
  'userDiagnostics.refresh.field.accountEnabled': 'Cuenta habilitada',
  'userDiagnostics.refresh.field.mail': 'Dirección de correo',
  'userDiagnostics.refresh.field.postalCode': 'Código postal',
  'userDiagnostics.refresh.field.objectId': 'Id. de objeto de Entra',
  'userDiagnostics.refresh.field.department': 'Departamento',
  'userDiagnostics.refresh.field.jobTitle': 'Puesto',
  'userDiagnostics.refresh.field.officeLocation': 'Oficina',
  'userDiagnostics.refresh.field.usageLocation': 'Ubicación de uso',
  'userDiagnostics.refresh.field.country': 'País o región',
  'userDiagnostics.refresh.field.stateOrProvince': 'Estado o provincia',
  'userDiagnostics.refresh.field.companyName': 'Empresa',
  'userDiagnostics.refresh.field.manager': 'Responsable',

  'userDiagnostics.live.title': 'Comparar con Microsoft Graph',
  'userDiagnostics.live.description':
    'Lee ahora del directorio las licencias de esta persona, con el registro de aplicación de la solución, y las compara con lo que contiene la base de datos. Solo lectura: no se modifica nada.',
  'userDiagnostics.live.button': 'Comparar con Microsoft Graph',
  'userDiagnostics.live.loading': 'Leyendo el directorio...',
  'userDiagnostics.live.notAllowed.scopeExcluded': 'No disponible: esta persona está fuera del ámbito de usuarios configurado.',
  'userDiagnostics.live.notAllowed.scopeUnknown': 'No disponible hasta que se pueda resolver el ámbito de usuarios.',
  'userDiagnostics.live.failed.userNotFoundInDirectory': 'Microsoft Graph no tiene ningún usuario con esta identidad. Puede que se haya eliminado o cambiado de nombre.',
  'userDiagnostics.live.failed.permissionDenied': 'Microsoft Graph rechazó la solicitud (HTTP {status}). El registro de aplicación necesita el permiso de aplicación User.Read.All.',
  'userDiagnostics.live.failed.graphUnavailable': 'No se pudo leer Microsoft Graph en este momento. Vuelva a intentarlo en breve.',
  'userDiagnostics.live.failed.credentialsUnavailable': 'No se pudieron cargar las credenciales del registro de aplicación de la solución.',
  'userDiagnostics.live.failed.timeout': 'Microsoft Graph no respondió a tiempo. Vuelva a intentarlo en breve.',
  'userDiagnostics.live.failed.identityMismatch': 'Microsoft Graph devolvió otra persona para este nombre, así que no se hizo ninguna comparación.',
  'userDiagnostics.live.observedAt': 'Observado {when}',
  'userDiagnostics.live.incomplete':
    'La respuesta del directorio estaba incompleta, así que una licencia que no aparece en ella no demuestra que la base de datos esté mal. Revise las filas marcadas como «No se puede saber».',
  'userDiagnostics.live.renamed': 'El directorio conoce ahora a esta persona como {upn}.',
  'userDiagnostics.live.objectIdMismatch': 'El id. de objeto del directorio no coincide con el guardado: puede que sean cuentas distintas.',
  'userDiagnostics.live.columnLicence': 'Licencia',
  'userDiagnostics.live.columnStored': 'En la base de datos',
  'userDiagnostics.live.columnLive': 'En el directorio',
  'userDiagnostics.live.columnResult': 'Resultado',
  'userDiagnostics.live.columnDetail': 'Detalle',
  'userDiagnostics.live.none': 'Ni la base de datos ni el directorio tienen ninguna licencia para esta persona.',
  'userDiagnostics.live.comparison.match': 'Coincide',
  'userDiagnostics.live.comparison.storedOnly': 'Solo en la base de datos',
  'userDiagnostics.live.comparison.liveOnly': 'Solo en el directorio',
  'userDiagnostics.live.comparison.indeterminate': 'No se puede saber',
  'userDiagnostics.live.mapping.collapsed': 'Varias SKU comparten este nombre de licencia, así que la base de datos no puede distinguirlas.',
  'userDiagnostics.live.mapping.unmapped': 'El importador todavía no ha archivado esta SKU en ninguna licencia.',
  'userDiagnostics.live.mapping.catalogueDrift': 'Guardada con un nombre de producto anterior; la próxima importación la volverá a archivar con el actual.',
  'userDiagnostics.live.path.direct': 'Asignada directamente',
  'userDiagnostics.live.path.group': 'Asignada mediante un grupo',
  'userDiagnostics.live.assignmentError': 'Estado de la asignación {state}, error {error}',
  'userDiagnostics.live.copilotPlans.enabled': 'Planes del puesto de Copilot activados',
  'userDiagnostics.live.copilotPlans.disabled': 'Planes del puesto de Copilot desactivados para esta persona',
  'userDiagnostics.live.copilotPlans.pending': 'Planes del puesto de Copilot todavía no activos',

  'userDiagnostics.verdict.title': '¿Por qué un informe cuenta así a esta persona?',
  'userDiagnostics.verdict.description':
    'Vuelve a aplicar la regla de licencias de un informe solo a esta persona, con los datos guardados ahora. Si los datos han cambiado desde que vio el informe, el resultado puede ser distinto del que vio.',
  'userDiagnostics.verdict.report': 'Informe',
  'userDiagnostics.verdict.report.copilotAdoption': 'Adopción de Copilot',
  'userDiagnostics.verdict.report.licenceActivity': 'Actividad de licencias',
  'userDiagnostics.verdict.period': 'Periodo',
  'userDiagnostics.verdict.rollingDays': 'Últimos {days} días',
  'userDiagnostics.verdict.customRange': 'Del {from} al {to}',
  'userDiagnostics.verdict.fromReport': 'Se usan el periodo y la selección de puestos del informe del que viene.',
  'userDiagnostics.verdict.button': 'Explicar',
  'userDiagnostics.verdict.loading': 'Volviendo a aplicar la regla del informe...',
  'userDiagnostics.verdict.licensed': 'Con licencia',
  'userDiagnostics.verdict.unlicensed': 'Sin licencia',
  'userDiagnostics.verdict.basis.currentAssignments': 'Se decide con las licencias guardadas ahora, sea cual sea el periodo.',
  'userDiagnostics.verdict.basis.licenceHistory': 'Se decide con las licencias asignadas en cualquier momento del periodo, según el historial de licencias.',
  'userDiagnostics.verdict.seatSelection.automatic': 'Licencias de puesto elegidas por las reglas de clasificación.',
  'userDiagnostics.verdict.seatSelection.reportOverride': 'Licencias de puesto elegidas por la selección hipotética del informe.',
  'userDiagnostics.verdict.counted': 'Licencias que cuenta el informe',
  'userDiagnostics.verdict.noneCounted': 'Ninguna.',
  'userDiagnostics.verdict.deciding': 'Por qué tiene licencia',
  'userDiagnostics.verdict.noDeciding': 'Esta persona no tiene ninguna de las licencias contadas, así que el informe la trata como sin licencia.',
  'userDiagnostics.verdict.current': 'Asignada ahora',
  'userDiagnostics.verdict.held': 'Asignada del {from} al {to}',
  'userDiagnostics.verdict.heldOpen': 'Asignada desde el {from}, sigue asignada',
  'userDiagnostics.verdict.recomputed': 'Recalculado {when} con los datos guardados ahora.',
  'userDiagnostics.verdict.note.noSeatLicenceTypes': 'Ninguna licencia cuenta como puesto de Copilot, así que nadie puede tener licencia en este informe.',
  'userDiagnostics.verdict.note.historyPartialForPeriod':
    'El periodo empieza antes de que comenzara el historial de licencias. Las asignaciones anteriores se desconocen; las licencias que ya estaban asignadas cuando empezó el historial cuentan desde el inicio del periodo.',
  'userDiagnostics.verdict.note.historyUnavailableForPeriod': 'Es un periodo pasado, pero la base de datos no tiene historial de licencias, así que se usaron las licencias guardadas ahora.',
  'userDiagnostics.verdict.note.rollingWindowUsesCurrentAssignments':
    'Un periodo móvil usa las licencias guardadas ahora: a quien se le retiró la licencia ayer se le cuenta como sin licencia durante todo el periodo.',
  'userDiagnostics.verdict.note.licenceActivityUsesCurrentAssignments': 'Actividad de licencias siempre usa las licencias guardadas ahora; el periodo solo selecciona la actividad.',
  'userDiagnostics.verdict.note.guestExcludedFromUnlicensedLists': 'Es una cuenta de invitado. Los invitados quedan fuera de las listas de usuarios sin licencia y de oportunidades de licencia.',
  'userDiagnostics.verdict.note.noMatchingLicenceTypes': 'Las licencias seleccionadas ya no existen.',
  'userDiagnostics.verdict.showSql': 'Mostrar la consulta',
  'userDiagnostics.verdict.copySql': 'Copiar la consulta',
  'userDiagnostics.verdict.sqlCopied': 'Consulta copiada al portapapeles',

  'userDiagnostics.rules.title': 'Reglas y catálogo de puestos de Copilot',
  'userDiagnostics.rules.description':
    'Qué licencias cuentan como puestos de Microsoft 365 Copilot y por qué. Se marcan las filas en las que el catálogo y las reglas no coinciden: puede que las reglas estén desactualizadas para esa SKU. Aquí no se modifican las reglas; pruebe una selección hipotética de puestos en Adopción de Copilot.',
  'userDiagnostics.rules.version': 'Versión de reglas {version}, compilación {build}, catálogo de licencias {hash} ({count} SKU)',
  'userDiagnostics.rules.seatPrefixes': 'Prefijos de SKU que se cuentan',
  'userDiagnostics.rules.excludedPrefixes': 'Prefijos de SKU excluidos',
  'userDiagnostics.rules.seatPlans': 'Planes de servicio que forman un puesto de Copilot en el catálogo',
  'userDiagnostics.rules.columnAssigned': 'Asignadas',
  'userDiagnostics.rules.gap': 'Las reglas y el catálogo no coinciden',
  'userDiagnostics.rules.show': 'Mostrar todas las licencias del inquilino ({count})',
  'userDiagnostics.rules.hide': 'Ocultar la lista de licencias',

  'userDiagnostics.coverage.title': 'Datos guardados y antigüedad de las importaciones',
  'userDiagnostics.coverage.description':
    'El registro más reciente de cada tipo guardado para esta persona y lo que se sabe de la importación que lo alimenta. No tener registros no es lo mismo que no tener actividad: puede que el origen esté desactivado, fuera del ámbito o todavía sin importar.',
  'userDiagnostics.coverage.load': 'Comprobar la antigüedad de los datos',
  'userDiagnostics.coverage.loading': 'Leyendo el registro más reciente de cada tipo...',
  'userDiagnostics.coverage.columnData': 'Datos',
  'userDiagnostics.coverage.columnNewest': 'Registro más reciente',
  'userDiagnostics.coverage.columnSource': 'Importación que lo alimenta (todo el inquilino)',
  'userDiagnostics.coverage.columnReplay': '¿Se puede volver a leer para una persona?',
  'userDiagnostics.coverage.none': 'Ninguno guardado',
  'userDiagnostics.coverage.importOff': 'Importación desactivada',
  'userDiagnostics.coverage.readFailed': 'No se pudo leer',
  'userDiagnostics.coverage.kind.eventTime': 'Cuándo ocurrió',
  'userDiagnostics.coverage.kind.reportDate': 'Día del informe; última actividad {lastActivity}',
  'userDiagnostics.coverage.kind.reportDateNoActivity': 'Día del informe; sin actividad notificada',
  'userDiagnostics.coverage.kind.observedDate': 'Cuándo lo observó la importación',
  'userDiagnostics.coverage.source.recorded': 'Completada por última vez {when}',
  'userDiagnostics.coverage.source.notRecorded': 'No hay ninguna finalización registrada',
  'userDiagnostics.coverage.source.noStampForSource': 'No se registra por ejecución; consulte la página Estado',
  'userDiagnostics.coverage.source.storageNotConfigured': 'No disponible sin Azure Storage',
  'userDiagnostics.coverage.source.storageUnavailable': 'No se pudo leer Azure Table Storage',
  'userDiagnostics.coverage.replay.perUserPossible': 'El origen responde por persona, pero hoy solo la importación programada lo vuelve a leer',
  'userDiagnostics.coverage.replay.sourceWideOnly': 'No: solo se puede volver a leer el origen completo',
  'userDiagnostics.coverage.replay.notReconstructable': 'No: la actividad que no se capturó no se puede recuperar',
  'userDiagnostics.coverage.category.copilot-usage-report': 'Informe de uso de Copilot (Microsoft)',

  'userDiagnostics.troubleshoot': 'Diagnosticar a esta persona',
  'userDiagnostics.troubleshootHint': 'Abre el diagnóstico de licencias y datos de esta persona en Búsqueda de usuario.',
  'userDiagnostics.noStoredUserLookup': 'Esta persona no está en la base de datos, así que solo está disponible el diagnóstico de abajo.',
};

export default userDiagnostics;
