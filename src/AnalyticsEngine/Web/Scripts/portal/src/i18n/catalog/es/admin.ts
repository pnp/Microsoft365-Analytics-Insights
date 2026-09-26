import type { admin as en } from '../en/admin';

/**
 * Spanish (es-ES) text for the administration pages: service configuration, Teams permissions, user lookup, profiling and the install log.
 *
 * Typed against the English module, so a key added there without a translation here fails the
 * build rather than reaching a customer as English text inside a Spanish page.
 */
const admin: Record<keyof typeof en, string> = {
  // Shared administration labels.
  'admin.common.enabled': 'Habilitado',
  'admin.common.no': 'No',
  'admin.common.unknown': 'Desconocido',
  'admin.common.unknownWithPeriod': 'Desconocido.',
  'admin.common.yes': 'Sí',

  // Teams permissions.
  'admin.teamsPermissions.description':
    'Esta página permite autorizar análisis detallados para un equipo. Esto permitirá a Microsoft 365 Advanced Analytics and Insights leer mensajes únicamente con fines de informes estadísticos anónimos.',
  'admin.teamsPermissions.errors.fetchGraphProfile': 'No se pudo obtener el perfil de Graph.',
  'admin.teamsPermissions.errors.fetchJoinedTeams': 'No se pudieron obtener los Teams unidos.',
  'admin.teamsPermissions.loadingTeams': 'Cargando sus Teams...',
  'admin.teamsPermissions.noTeamsFound': 'No se encontraron Teams para su cuenta.',
  'admin.teamsPermissions.noTokenMessage':
    'El sitio no pudo obtener un token de Microsoft Graph para su sesión, por lo que no se pueden enumerar sus Teams. Esto normalmente significa que el inicio de sesión que capturó el token de actualización ha expirado o es anterior a este cambio: cierre la sesión e iníciela de nuevo. Si sigue ocurriendo, compruebe que el registro de aplicación en tiempo de ejecución tiene los permisos delegados de Teams y que la URL de respuesta del sitio está registrada.',
  'admin.teamsPermissions.noTokenTeamsPlaceholder':
    'Sus Teams se mostrarán aquí cuando el sitio pueda obtener un token de Graph para su sesión.',
  'admin.teamsPermissions.title': 'Conceder acceso de equipo a Microsoft 365 Advanced Analytics Engine',
  'admin.teamsPermissions.tokenNote':
    'Nota: los tokens se almacenan de forma segura en una caché temporal de Redis y nadie puede acceder a ellos.',
  'admin.teamsPermissions.yourTeamsDescription':
    'Estos son todos los Teams a los que tiene acceso. Seleccione los Teams que desea habilitar para análisis detallados y continúe.',
  'admin.teamsPermissions.yourTeamsTitle': 'Sus Teams - {displayName}',

  // Teams authorisation list.
  'admin.teams.confirmSelection.actionsToApply': 'Acciones que se aplicarán:',
  'admin.teams.confirmSelection.saveChanges': 'Guardar cambios',
  'admin.teams.confirmSelection.summary':
    'Quitar autorización de {deAuthCount} Team(s); autorizar {authCount} Team(s)',
  'admin.teams.teamList.ariaLabel': 'Teams',
  'admin.teams.teamList.columnAuthorised': '¿Autorizado?',
  'admin.teams.teamList.columnGraphId': 'Id. de Graph',
  'admin.teams.teamList.columnTeamName': 'Nombre del equipo',
  'admin.teams.teamList.saveSuccess':
    'Los Teams seleccionados se han habilitado correctamente para análisis detallados. Los metadatos adicionales pueden tardar varias horas en aparecer en los informes.',
  'admin.teams.teamList.redisNotConfigured': 'No se pueden habilitar los análisis detallados de Teams porque Redis no está configurado en esta implementación. Añada una cadena de conexión de Redis para poder almacenar los tokens de autorización de Teams.',
  'admin.teams.teamList.unexpectedApiResponse':
    'Respuesta inesperada de la API. Compruebe el registro de JS para obtener más detalles.',
  'admin.teams.teamListItem.authorised': 'Autorizado',
  'admin.teams.teamListItem.notAuthorised': 'No autorizado',
  'admin.teams.teamListItem.unnamedTeam': '(equipo sin nombre)',

  // User data lookup page.
  'admin.userLookup.page.description':
    'Escriba el UPN de un usuario (nombre principal de usuario, por ejemplo, {exampleUpn}) para ver todos los datos que se conservan para él en la base de datos de análisis.',
  'admin.userLookup.page.exampleUpn': 'jane.doe@contoso.com',
  'admin.userLookup.page.loading': 'Consultando datos de usuario...',
  'admin.userLookup.page.lookupButton': 'Consultar',
  'admin.userLookup.page.lookupFailed': 'Error en la consulta.',
  'admin.userLookup.page.noUserLookedUp': 'Todavía no se ha consultado ningún usuario.',
  'admin.userLookup.page.title': 'Consulta de datos de usuario',
  'admin.userLookup.page.upnAriaLabel': 'Nombre principal de usuario',
  'admin.userLookup.page.upnPlaceholder': 'usuario@contoso.com',

  // User profile card.
  'admin.userLookup.profile.accountEnabled': 'Cuenta habilitada',
  'admin.userLookup.profile.azureAdId': 'Id. de Azure AD',
  'admin.userLookup.profile.company': 'Empresa',
  'admin.userLookup.profile.countryOrRegion': 'País o región',
  'admin.userLookup.profile.department': 'Departamento',
  'admin.userLookup.profile.jobTitle': 'Cargo',
  'admin.userLookup.profile.lastUpdatedUtc': 'Última actualización (UTC)',
  'admin.userLookup.profile.licensesTitle': 'Licencias ({count})',
  'admin.userLookup.profile.manager': 'Responsable',
  'admin.userLookup.profile.noLicenses': 'No hay licencias registradas.',
  'admin.userLookup.profile.office': 'Oficina',
  'admin.userLookup.profile.postalCode': 'Código postal',
  'admin.userLookup.profile.stateOrProvince': 'Estado o provincia',
  'admin.userLookup.profile.title': 'Perfil',
  'admin.userLookup.profile.usageLocation': 'Ubicación de uso',
  'admin.userLookup.profile.utcContractWarning':
    'Los valores escritos antes de este contrato UTC pueden reflejar la antigua hora local del host del trabajo web.',

  // User data category summary.
  'admin.userLookup.categoryTable.dataHeldTitle':
    'Datos conservados ({records} registros en {categories} categorías)',
  'admin.userLookup.categoryTable.importWorkloadsHint':
    'Los datos solo se recopilan para cargas de trabajo habilitadas. Una categoría alimentada únicamente por cargas de trabajo deshabilitadas mostrará 0 registros: es lo esperado, no un fallo.',
  'admin.userLookup.categoryTable.importWorkloadsTitle':
    'Cargas de trabajo de importación ({enabledCount} de {totalCount} habilitadas)',
  'admin.userLookup.categoryTable.sqlHint':
    'Haga clic en el botón {sql} de cualquier fila para ver y copiar la consulta que hay detrás de su recuento.',
  'admin.userLookup.workload.ActivityLog.name': 'Registro de auditoría',
  'admin.userLookup.workload.ActivityLog.description': 'Actividad de auditoría de SharePoint / Exchange / Entra ID (fuente Audit.SharePoint).',
  'admin.userLookup.workload.Copilot.name': 'Copilot y Power Platform',
  'admin.userLookup.workload.Copilot.description': 'Interacciones de Copilot y eventos de Power Platform (fuente Audit.General).',
  'admin.userLookup.workload.WebTraffic.name': 'Tráfico web',
  'admin.userLookup.workload.WebTraffic.description': 'Vistas de páginas, Me gusta y comentarios de SharePoint (rastreador de App Insights).',
  'admin.userLookup.workload.SentEmails.name': 'Correos enviados',
  'admin.userLookup.workload.SentEmails.description': 'Mensajes enviados desde buzones (Graph).',
  'admin.userLookup.workload.GraphTeams.name': 'Teams',
  'admin.userLookup.workload.GraphTeams.description': 'Pertenencias a equipos, propietarios, canales y reacciones (Graph).',
  'admin.userLookup.workload.Calls.name': 'Llamadas de Teams',
  'admin.userLookup.workload.Calls.description': 'Registros de llamadas de Teams: sesiones organizadas y asistidas.',
  'admin.userLookup.workload.GraphUsageReports.name': 'Informes de uso',
  'admin.userLookup.workload.GraphUsageReports.description': 'Informes diarios de actividad por usuario (Outlook, OneDrive, SharePoint, Teams, Viva Engage).',
  'admin.userLookup.workload.GraphUsersMetadata.name': 'Metadatos de usuario',
  'admin.userLookup.workload.GraphUsersMetadata.description': 'Metadatos de perfil de usuario: departamento, cargo, licencias, responsable y ubicación.',

  // User data category rows.
  'admin.userLookup.categoryRow.columnDetail': 'Detalle',
  'admin.userLookup.categoryRow.columnWhen': 'Cuándo',
  'admin.userLookup.categoryRow.copyToClipboard': 'Copiar al Portapapeles',
  'admin.userLookup.categoryRow.hideRecent': 'Ocultar',
  'admin.userLookup.categoryRow.importOff': 'importación desactivada',
  'admin.userLookup.categoryRow.importOffTooltip.one':
    'Estos datos no se están importando (carga de trabajo "{workloads}" deshabilitada), por lo que se espera un recuento de 0.',
  'admin.userLookup.categoryRow.importOffTooltip.other':
    'Estos datos no se están importando (cargas de trabajo "{workloads}" deshabilitadas), por lo que se espera un recuento de 0.',
  'admin.userLookup.categoryRow.loadDetailFailed': 'No se pudo cargar el detalle.',
  'admin.userLookup.categoryRow.loadingRecentRows': 'Cargando filas recientes...',
  'admin.userLookup.categoryRow.noRows': 'No hay filas.',
  'admin.userLookup.categoryRow.recentRowsAriaLabel': 'Filas recientes de {category}',
  'admin.userLookup.categoryRow.showingRecent': 'Mostrando {count} más recientes de {total}.',
  'admin.userLookup.categoryRow.source': 'Origen: {source}',
  'admin.userLookup.categoryRow.sourceNotAvailable': 'N/D',
  'admin.userLookup.categoryRow.sqlCopied': 'SQL copiado al Portapapeles',
  'admin.userLookup.categoryRow.sqlCopyFailed': 'No se pudo copiar al Portapapeles',
  'admin.userLookup.categoryRow.sqlTitle': 'SQL para reproducir este recuento',
  'admin.userLookup.categoryRow.viewRecent': 'Ver recientes',



  // Server-authored user data category labels.
  'admin.userLookup.category.audit-events.label': 'Eventos de auditoría (todos, total)',
  'admin.userLookup.category.audit-events.description': 'Total de toda la actividad de auditoría. Las filas específicas de carga de trabajo siguientes (Copilot, SharePoint, Power Platform, ...) desglosan este total; su suma puede ser inferior al total porque algunos eventos no tienen metadatos específicos de carga de trabajo.',
  'admin.userLookup.category.copilot-interactions.label': 'Interacciones de Copilot',
  'admin.userLookup.category.copilot-interactions.description': 'Interacciones de Microsoft 365 Copilot (entregadas mediante la fuente Audit.General).',
  'admin.userLookup.category.audit-sharepoint.label': 'Auditoría de SharePoint / OneDrive',
  'admin.userLookup.category.audit-sharepoint.description': 'Eventos de auditoría de SharePoint y OneDrive (acceso a archivos, uso compartido, etc.).',
  'admin.userLookup.category.audit-exchange.label': 'Auditoría de Exchange',
  'admin.userLookup.category.audit-exchange.description': 'Eventos de auditoría de Exchange / buzón.',
  'admin.userLookup.category.audit-entra.label': 'Auditoría de Entra ID',
  'admin.userLookup.category.audit-entra.description': 'Eventos de auditoría de Entra ID (Azure AD).',
  'admin.userLookup.category.audit-general.label': 'Auditoría general',
  'admin.userLookup.category.audit-general.description': "Otros eventos de auditoría de carga de trabajo 'general' (fuente Audit.General).",
  'admin.userLookup.category.powerapp-events.label': 'Eventos de Power Apps',
  'admin.userLookup.category.powerapp-events.description': 'Eventos de inicio / uso de Power Apps (fuente Audit.General).',
  'admin.userLookup.category.flow-events.label': 'Eventos de Power Automate',
  'admin.userLookup.category.flow-events.description': 'Eventos de ciclo de vida y permisos de Power Automate (fuente Audit.General).',
  'admin.userLookup.category.powerbi-events.label': 'Eventos de Power BI',
  'admin.userLookup.category.powerbi-events.description': 'Eventos de auditoría de Power BI (fuente Audit.General).',
  'admin.userLookup.category.copilot-studio-events.label': 'Eventos de Copilot Studio',
  'admin.userLookup.category.copilot-studio-events.description': 'Eventos de auditoría de Copilot Studio (bot) (fuente Audit.General).',
  'admin.userLookup.category.sent-emails.label': 'Correos electrónicos enviados',
  'admin.userLookup.category.sent-emails.description': 'Mensajes enviados desde el buzón del usuario.',
  'admin.userLookup.category.web-hits.label': 'Visitas a páginas web',
  'admin.userLookup.category.web-hits.description': 'Páginas vistas de SharePoint capturadas por el rastreador de tráfico web.',
  'admin.userLookup.category.team-memberships.label': 'Pertenencias a equipos',
  'admin.userLookup.category.team-memberships.description': 'Teams de los que el usuario ha sido miembro.',
  'admin.userLookup.category.team-ownerships.label': 'Propiedades de equipos',
  'admin.userLookup.category.team-ownerships.description': 'Teams que el usuario posee / ha poseído.',
  'admin.userLookup.category.teams-reactions.label': 'Reacciones de Teams',
  'admin.userLookup.category.teams-reactions.description': 'Reacciones realizadas por el usuario en mensajes de canal de Teams.',
  'admin.userLookup.category.calls-organised.label': 'Llamadas / reuniones organizadas',
  'admin.userLookup.category.calls-organised.description': 'Llamadas / reuniones de Teams que el usuario organizó.',
  'admin.userLookup.category.call-sessions.label': 'Sesiones de llamada asistidas',
  'admin.userLookup.category.call-sessions.description': 'Sesiones de llamada de Teams a las que asistió el usuario.',
  'admin.userLookup.category.call-feedback.label': 'Comentarios de llamada',
  'admin.userLookup.category.call-feedback.description': 'Comentarios de calidad de llamada registrados para el usuario.',
  'admin.userLookup.category.page-likes.label': 'Me gusta de página',
  'admin.userLookup.category.page-likes.description': 'Me gusta de páginas de SharePoint del usuario.',
  'admin.userLookup.category.page-comments.label': 'Comentarios de página',
  'admin.userLookup.category.page-comments.description': 'Comentarios de páginas de SharePoint del usuario.',
  'admin.userLookup.category.usage-outlook.label': 'Uso de Outlook (diario)',
  'admin.userLookup.category.usage-outlook.description': 'Filas del informe diario de actividad de Outlook.',
  'admin.userLookup.category.usage-onedrive.label': 'Uso de OneDrive (diario)',
  'admin.userLookup.category.usage-onedrive.description': 'Filas del informe diario de actividad de OneDrive.',
  'admin.userLookup.category.usage-sharepoint.label': 'Uso de SharePoint (diario)',
  'admin.userLookup.category.usage-sharepoint.description': 'Filas del informe diario de actividad de SharePoint.',
  'admin.userLookup.category.usage-yammer.label': 'Uso de Viva Engage (diario)',
  'admin.userLookup.category.usage-yammer.description': 'Filas del informe diario de actividad de Viva Engage (Yammer).',
  'admin.userLookup.category.usage-teams.label': 'Uso de Teams (diario)',
  'admin.userLookup.category.usage-teams.description': 'Filas del informe diario de actividad de Teams.',
  'admin.userLookup.category.usage-teams-device.label': 'Uso de dispositivos de Teams (diario)',
  'admin.userLookup.category.usage-teams-device.description': 'Filas del informe diario de actividad de dispositivos de Teams.',
  'admin.userLookup.category.usage-app-platform.label': 'Uso de plataforma de aplicaciones (diario)',
  'admin.userLookup.category.usage-app-platform.description': 'Filas del informe diario de actividad por aplicación y plataforma.',
  'admin.userLookup.category.powerapp-shares.label': 'Recursos compartidos de Power App recibidos',
  'admin.userLookup.category.powerapp-shares.description': 'Power Apps compartidas con el usuario.',
  'admin.userLookup.category.flow-shares.label': 'Recursos compartidos de Flow recibidos',
  'admin.userLookup.category.flow-shares.description': 'Flujos de Power Automate compartidos con el usuario.',
  'admin.userLookup.detail.callsOrganised.title': 'Llamada / reunión',
  'admin.userLookup.detail.callSessions.title': 'Sesión de llamada asistida',
  'admin.userLookup.detail.usage.title': 'Día del informe de actividad',
  'admin.userLookup.detail.auditEventFallback.title': '(evento de auditoría)',
  'admin.userLookup.detail.ended': 'Finalizó {date}',
  'admin.userLookup.detail.lastActivity': 'Última actividad {date}',

  // Service configuration: page and webhook status.
  'admin.serviceConfiguration.loadFailed': 'No se pudo cargar la configuración del servicio.',
  'admin.serviceConfiguration.loading': 'Cargando configuración del servicio...',
  'admin.serviceConfiguration.noConfiguration': 'No hay ninguna configuración disponible.',
  'admin.serviceConfiguration.title': 'Configuración del servicio',
  'admin.serviceConfiguration.titleWithBuild': 'Configuración del servicio - {buildLabel}',
  'admin.serviceConfiguration.webhook.active': 'Activo',
  'admin.serviceConfiguration.webhook.callRecordsPermission': 'CallRecords.Read.All',
  'admin.serviceConfiguration.webhook.couldNotCheck': 'No se pudo comprobar',
  'admin.serviceConfiguration.webhook.detail.webAppUrlMissing': 'WebAppURL no está configurado, por lo que no se puede determinar la dirección URL de suscripción del webhook.',
  'admin.serviceConfiguration.webhook.missingHelp':
    'El trabajo web de importación registra y renueva esta suscripción en cada ciclo de importación. Si sigue faltando, compruebe que el trabajo web de importación se está ejecutando y que su registro de aplicación tiene el permiso de aplicación {permission} de Microsoft Graph.',
  'admin.serviceConfiguration.webhook.noActiveSubscription': 'No se encontró ninguna suscripción activa',
  'admin.serviceConfiguration.webhook.notApplicable':
    'No aplicable: la importación de llamadas de Teams está deshabilitada',
  'admin.serviceConfiguration.webhook.renewsAutomatically': 'se renueva automáticamente; expira el {expiry}',
  'admin.serviceConfiguration.webhook.testFailed': 'Error en la prueba del webhook.',
  'admin.serviceConfiguration.webhook.testSuccess': 'Correcto. Se recibió el test-token "{token}"',
  'admin.serviceConfiguration.webhook.testUnexpectedResponse':
    'Respuesta inesperada. Se recibió el cuerpo de respuesta "{token}"',

  // Service configuration: updates.
  'admin.serviceConfiguration.updates.ariaLabel': 'Comprobación de actualizaciones',
  'admin.serviceConfiguration.updates.build': 'Compilación {build}',
  'admin.serviceConfiguration.updates.checked': 'Comprobado {checkedAt}',
  'admin.serviceConfiguration.updates.checkFailed': 'Error al comprobar actualizaciones.',
  'admin.serviceConfiguration.updates.checkForUpdates': 'Buscar actualizaciones',
  'admin.serviceConfiguration.updates.checking': 'Comprobando...',
  'admin.serviceConfiguration.updates.currentBuildLabel': 'Este sitio ejecuta',
  'admin.serviceConfiguration.updates.description':
    'Compara la compilación que ejecuta este sitio con la versión publicada más reciente en GitHub. No se envía nada a GitHub hasta que pulse el botón.',
  'admin.serviceConfiguration.updates.error.timeout': 'Se agotó el tiempo de espera tras {seconds} s al contactar con github.com. Si esta aplicación web no tiene acceso saliente a Internet (por ejemplo, una implementación con punto de conexión privado y salida restringida), la comprobación de actualizaciones no puede funcionar desde aquí: consulte manualmente la página de versiones.',
  'admin.serviceConfiguration.updates.error.unreachable': 'No se pudo contactar con github.com para buscar actualizaciones: {error}. Es lo esperable si la aplicación web no tiene acceso saliente a Internet; consulte manualmente la página de versiones.',
  'admin.serviceConfiguration.updates.error.failed': 'Error al comprobar actualizaciones: {error}',
  'admin.serviceConfiguration.updates.error.devBuild': 'Esta es una compilación local (DEV_BUILD), por lo que no tiene número de compilación con el que comparar. Se muestra la última versión publicada como referencia.',
  'admin.serviceConfiguration.updates.error.currentBuildUnreadable': "No se pudo leer un número de compilación en la etiqueta de esta compilación ('{label}'), por lo que no se puede comparar. Se muestra la última versión publicada como referencia.",
  'admin.serviceConfiguration.updates.error.latestBuildUnreadable': 'No se pudo leer un número de compilación en la última versión de GitHub, por lo que no se pueden comparar. Abra la página de versiones para comprobarlo manualmente.',
  'admin.serviceConfiguration.updates.error.rateLimited': 'GitHub rechazó la solicitud porque se alcanzó el límite de solicitudes de su API, no por un problema de permisos. El límite se restablece el {resetAt}. Las solicitudes anónimas están limitadas a 60 por hora por dirección IP pública, que comparte todo lo que hay detrás de su dirección de salida. Vuelva a intentarlo después del restablecimiento.',
  'admin.serviceConfiguration.updates.error.rateLimitedSoon': 'GitHub rechazó la solicitud porque se alcanzó el límite de solicitudes de su API, no por un problema de permisos. El límite se restablecerá en breve. Las solicitudes anónimas están limitadas a 60 por hora por dirección IP pública, que comparte todo lo que hay detrás de su dirección de salida. Vuelva a intentarlo después del restablecimiento.',
  'admin.serviceConfiguration.updates.error.releasesNotFound': 'GitHub devolvió 404 para el punto de conexión de versiones. Si esta implementación está detrás de un proxy que intercepta HTTPS, es posible que el proxy devuelva su propia respuesta en lugar de la de GitHub.',
  'admin.serviceConfiguration.updates.error.httpStatus': 'GitHub devolvió {status} ({statusName}) al solicitar la última versión.',
  'admin.serviceConfiguration.updates.latestReleaseLabel': 'Versión publicada más reciente',
  'admin.serviceConfiguration.updates.openLatestRelease': 'Abrir la versión más reciente',
  'admin.serviceConfiguration.updates.openReleaseNotes': 'Abrir las notas de la versión y las descargas',
  'admin.serviceConfiguration.updates.published': 'Publicado {publishedAt}',
  'admin.serviceConfiguration.updates.title': 'Actualizaciones de software',
  'admin.serviceConfiguration.updates.updateAvailableLead': 'Hay una actualización disponible.',
  'admin.serviceConfiguration.updates.updateAvailableNoLink':
    '{lead} Este sitio está en la compilación {currentBuild}; se ha publicado la compilación {latestBuild}. Lea las notas de la versión antes de actualizar: indican cualquier migración de base de datos y cambio de configuración.',
  'admin.serviceConfiguration.updates.updateAvailableWithLink':
    '{lead} Este sitio está en la compilación {currentBuild}; se ha publicado la compilación {latestBuild}. {releaseLink}. Lea las notas de la versión antes de actualizar: indican cualquier migración de base de datos y cambio de configuración.',
  'admin.serviceConfiguration.updates.upToDate':
    'Este sitio está actualizado: no se ha publicado ninguna versión más reciente.',
  'admin.serviceConfiguration.updates.viewCurrentRelease': 'Ver la versión actual',

  // Service configuration: Azure resources.
  'admin.serviceConfiguration.azureResources.ariaLabel': 'Recursos de Azure',
  'admin.serviceConfiguration.azureResources.cognitiveAnalyticsAvailable':
    'Sí: los análisis cognitivos estarán disponibles',
  'admin.serviceConfiguration.azureResources.cognitiveAnalyticsDisabled':
    'No: los análisis cognitivos están deshabilitados',
  'admin.serviceConfiguration.azureResources.cognitiveServicesEnabled': 'Cognitive Services habilitado',
  'admin.serviceConfiguration.azureResources.cognitiveServicesEndpoint': 'Punto de conexión de Cognitive Services',
  'admin.serviceConfiguration.azureResources.description':
    'Estos son los recursos que esta implementación está configurada para usar:',
  'admin.serviceConfiguration.azureResources.redisSslEndpoint': 'Punto de conexión SSL de Redis',
  'admin.serviceConfiguration.azureResources.title': 'Recursos de Azure',
  'admin.serviceConfiguration.azureResources.webAppUrl': 'URL de la aplicación web',

  // Service configuration: imports and schema.
  'admin.serviceConfiguration.importsAndSchema.configLoadFailed': 'No se pudo cargar la configuración: {error}',
  'admin.serviceConfiguration.importsAndSchema.databaseBehind':
    'La base de datos va por detrás de esta compilación: ejecute el actualizador. ({migrations})',
  'admin.serviceConfiguration.importsAndSchema.description':
    'Qué cargas de trabajo de importación están activadas, para que un informe vacío se interprete como "característica desactivada", no como "rota".',
  'admin.serviceConfiguration.importsAndSchema.noneEnabled':
    'Ninguna habilitada en la configuración de esta aplicación.',
  'admin.serviceConfiguration.importsAndSchema.pendingMigrations': '{count} migración(es) pendiente(s)',
  'admin.serviceConfiguration.importsAndSchema.schemaCheckFailed': 'No se pudo comprobar: {error}',
  'admin.serviceConfiguration.importsAndSchema.schemaStateAriaLabel': 'Estado del esquema',
  'admin.serviceConfiguration.importsAndSchema.schemaVersion': 'Versión del esquema o migración',
  'admin.serviceConfiguration.importsAndSchema.title': 'Importaciones y esquema',
  'admin.serviceConfiguration.importsAndSchema.upToDate': 'Actualizado con esta compilación',

  // Service configuration: Teams calls.
  'admin.serviceConfiguration.teamsCalls.ariaLabel': 'Configuración de llamadas de Teams',
  'admin.serviceConfiguration.teamsCalls.disabled':
    'Deshabilitado: los registros de llamadas de Teams no se están importando',
  'admin.serviceConfiguration.teamsCalls.healthCheckExpiry':
    'La comprobación de estado lo vio expirar por última vez el {expiry}.',
  'admin.serviceConfiguration.teamsCalls.importLabel': 'Importación de llamadas de Teams',
  'admin.serviceConfiguration.teamsCalls.testWebhook': 'probar webhook con POST de validación',
  'admin.serviceConfiguration.teamsCalls.title': 'Llamadas de Teams',
  'admin.serviceConfiguration.teamsCalls.webhookEndpoint': 'Punto de conexión del webhook de llamadas de Graph',
  'admin.serviceConfiguration.teamsCalls.webhookSubscription': 'Suscripción del webhook de llamadas',

  // Profiling status.
  'admin.profiling.compiledData.description':
    'Creado por los runbooks de generación de perfiles. Si estos datos están vacíos u obsoletos, los runbooks no se han ejecutado (o han generado errores).',
  'admin.profiling.compiledData.title': 'Datos compilados de generación de perfiles',
  'admin.profiling.dataFreshness': 'Actualidad de los datos',
  'admin.profiling.description':
    'Estado actual de los datos de generación de perfiles: lo reciente que es cada tabla y el propio registro de seguimiento de los runbooks de generación de perfiles. Use esto para comprobar que los runbooks se han ejecutado y que los datos están actualizados.',
  'admin.profiling.errors.loadStatusFailed': 'No se pudo cargar el estado de la generación de perfiles.',
  'admin.profiling.errors.loadTraceLogsFailed': 'No se pudieron cargar los registros de seguimiento.',
  'admin.profiling.loadingStatus': 'Cargando estado de generación de perfiles...',
  'admin.profiling.rangeSection.columnData': 'Datos',
  'admin.profiling.rangeSection.columnEarliest': 'Más antiguo',
  'admin.profiling.rangeSection.columnLatest': 'Más reciente',
  'admin.profiling.rangeSection.sqlTitle': 'SQL para reproducir estas fechas',

  'admin.profiling.range.weekly-activities': 'Actividades semanales (filas)',
  'admin.profiling.range.weekly-activity-columns': 'Actividades semanales (columnas)',
  'admin.profiling.range.weekly-usage': 'Uso semanal',
  'admin.profiling.range.teams-user-activity': 'Actividad de usuarios de Teams',
  'admin.profiling.range.teams-device-usage': 'Uso de dispositivos de usuarios de Teams',
  'admin.profiling.range.outlook-user-activity': 'Actividad de usuarios de Outlook (correo electrónico)',
  'admin.profiling.range.onedrive-user-activity': 'Actividad de usuarios de OneDrive',
  'admin.profiling.range.sharepoint-user-activity': 'Actividad de usuarios de SharePoint',
  'admin.profiling.range.yammer-user-activity': 'Actividad de usuarios de Viva Engage (Yammer)',
  'admin.profiling.range.yammer-device-activity': 'Actividad de dispositivos de Viva Engage (Yammer)',
  'admin.profiling.range.platform-user-activity': 'Actividad de usuarios de aplicaciones de Microsoft 365',
  'admin.profiling.range.copilot-interactions': 'Interacciones de Copilot',
  'admin.profiling.refresh': 'Actualizar',
  'admin.profiling.sourceActivityData.description':
    'Tablas sin procesar del registro de actividad que alimentan la compilación de perfiles, importadas desde los informes de uso de Microsoft 365.',
  'admin.profiling.sourceActivityData.title': 'Datos de actividad de origen',
  'admin.profiling.title': 'Generación de perfiles',
  'admin.profiling.traceLogs.ariaLabel': 'Registros de seguimiento de generación de perfiles',
  'admin.profiling.traceLogs.columnMessage': 'Mensaje',
  'admin.profiling.traceLogs.columnWhen': 'Cuándo',
  'admin.profiling.traceLogs.description':
    'Salida de seguimiento escrita por los runbooks de generación de perfiles (profiling.TraceLogs), primero la más reciente.',
  'admin.profiling.traceLogs.loading': 'Cargando…',
  'admin.profiling.traceLogs.loadingTraceLogs': 'Cargando registros de seguimiento...',
  'admin.profiling.traceLogs.next': 'Siguiente',
  'admin.profiling.traceLogs.none': 'No hay registros de seguimiento.',
  'admin.profiling.traceLogs.noneOnPage': 'No hay registros de seguimiento en esta página.',
  'admin.profiling.traceLogs.previous': 'Anterior',
  'admin.profiling.traceLogs.readFailed': 'No se pudieron leer los registros de seguimiento de perfiles: {error}',
  'admin.profiling.traceLogs.rowsPerPage': 'Filas por página',
  'admin.profiling.traceLogs.showing': 'Mostrando {firstRow}–{lastRow} de {total}',
  'admin.profiling.traceLogs.sqlTitle': 'SQL detrás del registro de seguimiento',
  'admin.profiling.traceLogs.title': 'Registros de seguimiento',

  // Install log.
  'admin.installLog.ariaLabel': 'Registro de instalación',
  'admin.installLog.close': 'Cerrar',
  'admin.installLog.columnApplied': 'Aplicado',
  'admin.installLog.columnConfiguration': 'Configuración',
  'admin.installLog.columnInstalledBy': 'Instalado por',
  'admin.installLog.columnMessages': 'Mensajes',
  'admin.installLog.current': 'Actual',
  'admin.installLog.description':
    'Historial de configuraciones aplicadas a la solución (la tabla {table}). La entrada más reciente es la configuración actual.',
  'admin.installLog.dialogTitle': 'Registro de instalación — {appliedAt}',
  'admin.installLog.loadFailed': 'No se pudo cargar el registro de instalación.',
  'admin.installLog.loading': 'Cargando registro de instalación...',
  'admin.installLog.noneApplied': 'Todavía no se ha aplicado ninguna configuración.',
  'admin.installLog.title': 'Registro de instalación',
  'admin.installLog.viewConfig': 'Ver configuración',
  'admin.installLog.viewLog': 'Ver registro',
};

export default admin;
