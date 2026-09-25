import type { agentCosts as en } from '../en/agentCosts';

/**
 * Spanish (es-ES) text for the Agent costs page.
 *
 * Typed against the English module, so a key added there without a translation here fails the
 * build rather than reaching a customer as English text inside a Spanish page.
 */
const agentCosts: Record<keyof typeof en, string> = {
  // Estados y unidades compartidos
  'agentCosts.state.notReported': 'No informado',
  'agentCosts.unit.credits': 'créditos',

  // Valores de arnés
  'agentCosts.harness.standardOrCopilotChat': 'Estándar / Copilot Chat',
  'agentCosts.harness.unrecognisedFeature': 'Característica no reconocida',
  'agentCosts.harness.noFeatureReported': 'No se informó ninguna característica',

  // Dimensiones
  'agentCosts.dimension.credit.agent.label': 'Agente',
  'agentCosts.dimension.credit.agent.hint': 'Agente al que se facturaron los créditos.',
  'agentCosts.dimension.credit.environment.label': 'Entorno',
  'agentCosts.dimension.credit.environment.hint': 'Entorno de Power Platform donde reside el agente.',
  'agentCosts.dimension.credit.harness.label': 'Arnés',
  'agentCosts.dimension.credit.harness.hint': 'Standard / Copilot Chat o GitHub Copilot. Se deduce de la característica de facturación, porque Microsoft no lo informa directamente.',
  'agentCosts.dimension.credit.feature.label': 'Característica de facturación',
  'agentCosts.dimension.credit.feature.hint': 'Qué se cobró: una respuesta generativa, fundamentación con grafo del inquilino o una acción de agente.',
  'agentCosts.dimension.azure.meter': 'Medidor',
  'agentCosts.dimension.azure.service': 'Servicio',
  'agentCosts.dimension.azure.category': 'Categoría de medidor',
  'agentCosts.dimension.azure.resource': 'Recurso',
  'agentCosts.dimension.azure.resourceGroup': 'Grupo de recursos',
  'agentCosts.dimension.azure.subscription': 'Suscripción',
  'agentCosts.dimension.azure.tag': 'Valor de etiqueta',

  // Marco de la página
  'agentCosts.title': 'Costes de agentes',
  'agentCosts.intro': 'Lo que Microsoft cobró por los agentes de Copilot Studio, desglosado hasta donde permiten los datos de facturación: por agente, entorno, arnés y característica de facturación, además del gasto de Azure de las suscripciones que se importan. Los desgloses solo aparecen cuando Microsoft informa realmente de la dimensión correspondiente.',
  'agentCosts.loading': 'Cargando costes de agentes...',
  'agentCosts.window.last7Days': 'Últimos 7 días',
  'agentCosts.window.last30Days': 'Últimos 30 días',
  'agentCosts.window.last60Days': 'Últimos 60 días',
  'agentCosts.window.last90Days': 'Últimos 90 días',
  'agentCosts.window.last180Days': 'Últimos 180 días',

  // Errores y avisos
  'agentCosts.error.summary': 'No se han podido cargar las cifras de coste de agentes.',
  'agentCosts.error.breakdown': 'No se ha podido cargar el desglose de créditos. Cambie el periodo o actualice.',
  'agentCosts.error.detail': 'No se han podido cargar las líneas facturadas. Cambie el periodo o actualice.',
  'agentCosts.error.azure': 'No se ha podido cargar el desglose de costes de Azure. Cambie el periodo o actualice.',
  'agentCosts.error.export': 'No se han podido exportar las líneas facturadas.',
  'agentCosts.notice.exportedTruncated': 'Se han exportado las primeras {rows} de {totalRows} líneas facturadas. Acote los filtros o reduzca el periodo para exportar el resto.',
  'agentCosts.notice.exportedRows': 'Se han exportado {rows} línea(s) facturada(s).',
  'agentCosts.warning.copilotStudioImportFailing': 'La importación de créditos de Copilot Studio está fallando.',
  'agentCosts.warning.azureCostImportFailing': 'La importación de costes de Azure está fallando.',


  // Server-authored availability messages
  'agentCosts.availability.message.noImports': 'Ninguna importación de costes de agente está activada. Pida a quien instaló el producto que marque "Copilot Studio credits" y/o "Azure costs" en el instalador.',
  'agentCosts.availability.message.copilotImportFailing': 'La importación de créditos de Copilot Studio está activada, pero produce errores. Compruebe que el registro de aplicación tiene un rol de Power Platform en el ámbito del inquilino; pero, si ya lo tiene, lo más probable es que Microsoft esté rechazando el acceso de solo aplicación a la API de licencias, en lugar de que falte algo por configurar aquí. En ese caso, la importación no puede completarse actualmente y conviene desactivarla. El error fue: {error}',
  'agentCosts.availability.message.copilotNoUsage': 'La importación de créditos de Copilot Studio se ejecutó correctamente, pero no encontró uso facturado de agentes. Es el resultado esperado para un inquilino sin agentes de Copilot Studio, o sin agentes que hayan consumido créditos todavía.',
  'agentCosts.availability.message.copilotNotStoredYet': 'La importación de créditos de Copilot Studio está activada, pero aún no ha almacenado nada. Se ejecuta una vez al día, así que espere un ciclo antes de esperar cifras.',
  'agentCosts.availability.message.azureImportFailing': 'La importación de costes de Azure está activada, pero produce errores: {error}',
  'agentCosts.availability.message.azureNoSpend': 'La importación de costes de Azure se ejecutó correctamente, pero la consulta no encontró gasto. Compare el filtro de medidor con una exportación de costes del mismo ámbito: un filtro que no coincide con nada tiene exactamente el mismo aspecto que no tener gasto.',
  'agentCosts.availability.message.azureNotStoredYet': 'La importación de costes de Azure está activada, pero aún no ha almacenado nada. Compruebe que se ha establecido un ámbito y espere un ciclo antes de esperar cifras.',
  'agentCosts.availability.message.perUserNotUpdating': 'Las cifras de Copilot Studio por persona no se están actualizando: {error} Las cifras por agente anteriores no se ven afectadas, pero cualquier dato mostrado por persona puede estar obsoleto.',
  'agentCosts.availability.message.capacityNotUpdating': 'La instantánea de capacidad de Copilot Credits no se está actualizando: {error} Cualquier cifra de capacidad restante mostrada puede estar obsoleta.',
  'agentCosts.availability.message.creditEndpointMismatch': 'Microsoft informa el gasto de Copilot Studio de dos formas: por agente y por persona. Proceden de puntos de conexión de Microsoft diferentes, en lugar de que una sea un desglose de la otra, por lo que sus totales no siempre coincidirán exactamente. Los recuentos de usuarios por agente indican cuántas personas distintas usaron un agente; no se pueden sumar, porque la misma persona aparece bajo cada agente que utilizó.',
  'agentCosts.availability.message.azureNoPeople': 'Los costes de Azure no se pueden atribuir a personas individuales. Azure factura por recurso y ningún informe de facturación de Azure, incluida la exportación completa de costes, registra quién causó un cargo.',
  'agentCosts.availability.message.azureEstimates': 'Los costes de Azure son estimaciones hasta que Microsoft cierra el periodo de facturación, lo que puede tardar unos días después del fin de mes. Las cifras marcadas como estimaciones todavía pueden cambiar.',

  // Resumen de gasto
  'agentCosts.spend.title': 'Gasto en el periodo seleccionado',
  'agentCosts.spend.importedUtc': 'Importado el {when} UTC',
  'agentCosts.kpi.creditsBilled': 'Copilot Credits facturados',
  'agentCosts.kpi.creditsNotCharged': 'Créditos no cobrados',
  'agentCosts.kpi.creditsNotCharged.hint': 'Usados, pero cubiertos por una asignación',
  'agentCosts.kpi.agentsWithSpend': 'Agentes con gasto',
  'agentCosts.kpi.environments': 'Entornos',
  'agentCosts.kpi.busiestSlice': 'Segmento con más actividad',
  'agentCosts.kpi.busiestSlice.hint': 'Mayor número de personas en una sola línea facturada; nunca es el total de usuarios del inquilino',
  'agentCosts.kpi.unclassifiedHarness': 'Arnés sin clasificar',
  'agentCosts.kpi.unclassifiedHarness.hint': 'Microsoft informó de una característica que no reconocemos',
  'agentCosts.capacity.availableNow': 'Créditos disponibles ahora',
  'agentCosts.capacity.usedOfEntitled': '{consumed} de {entitled} usados',
  'agentCosts.capacity.status': 'Estado de capacidad',
  'agentCosts.capacity.status.monthToDate': 'Mes hasta la fecha',
  'agentCosts.capacity.status.withinCapacity': 'Dentro de la capacidad',
  'agentCosts.capacity.status.overage': 'Exceso',
  'agentCosts.capacity.asAt': 'A fecha de {day}',
  'agentCosts.capacity.payAsYouGo': 'Créditos de pago por uso',
  'agentCosts.capacity.payAsYouGo.hint': 'Facturados además de la capacidad comprada previamente',
  'agentCosts.azureSpend.title': 'Gasto de Azure',
  'agentCosts.azureSpend.meteredUnitsBilled': '{quantity} unidades medidas facturadas',
  'agentCosts.azureSpend.includesEstimates': 'Incluye estimaciones que aún pueden cambiar',

  // Tendencia
  'agentCosts.trend.title': 'Créditos por día',
  'agentCosts.trend.empty': 'No hay consumo de créditos en este periodo.',
  'agentCosts.trend.barTitle': '{day}: {credits} créditos',
  'agentCosts.trend.caption': '{from} a {to}, pico de {peak} créditos en un día',

  // Filtros
  'agentCosts.filters.title': 'Acotar las cifras por agente',
  'agentCosts.filter.agent': 'Agente',
  'agentCosts.filter.agent.all': 'Todos los agentes',
  'agentCosts.filter.environment': 'Entorno',
  'agentCosts.filter.environment.all': 'Todos los entornos',
  'agentCosts.filter.harness': 'Arnés',
  'agentCosts.filter.harness.all': 'Todos los arneses',
  'agentCosts.filter.billingFeature': 'Característica de facturación',
  'agentCosts.filter.billingFeature.all': 'Todas las características',
  'agentCosts.filter.searchAgentName': 'Buscar nombre de agente',
  'agentCosts.filter.searchAgentName.placeholder': 'Nombre o identificador de agente',

  // Desglose de créditos
  'agentCosts.breakdown.title': 'Dónde se consumieron los créditos',
  'agentCosts.breakdown.hiddenRows': 'Se muestran los {count} principales del gasto de este periodo. Los porcentajes son sobre el total de {credits} créditos, por lo que no sumarán el 100 %.',

  // Detalle de líneas facturadas
  'agentCosts.detail.title': 'Cada línea facturada',
  'agentCosts.detail.description': 'Una fila por día, agente y dimensión de facturación: la vista más detallada que permiten los datos de facturación de Microsoft. Haga clic en un encabezado de columna para ordenar.',
  'agentCosts.detail.exportPage': 'Exportar esta página',
  'agentCosts.detail.exporting': 'Exportando...',
  'agentCosts.detail.exportAllRows': 'Exportar las {rows} filas',
  'agentCosts.detail.empty': 'No hay líneas facturadas que coincidan con los filtros actuales.',
  'agentCosts.export.pageFilename': 'creditos-agentes-{from}-a-{to}-pagina{page}.csv',
  'agentCosts.export.filteredFilename': 'creditos-agentes-{from}-a-{to}-filtrado.csv',

  // Tablas y paginación
  'agentCosts.table.value': 'Valor',
  'agentCosts.table.creditsBilled': 'Créditos facturados',
  'agentCosts.table.share': 'Porcentaje',
  'agentCosts.table.notCharged': 'No cobrado',
  'agentCosts.table.daysActive': 'Días activos',
  'agentCosts.table.busiestSlicePeople': 'Segmento con más actividad (personas)',
  'agentCosts.table.day': 'Día',
  'agentCosts.table.agent': 'Agente',
  'agentCosts.table.environment': 'Entorno',
  'agentCosts.table.harness': 'Arnés',
  'agentCosts.table.billingFeature': 'Característica de facturación',
  'agentCosts.table.credits': 'Créditos',
  'agentCosts.table.people': 'Personas',
  'agentCosts.table.person': 'Persona',
  'agentCosts.table.cost': 'Coste',
  'agentCosts.table.quantity': 'Cantidad',
  'agentCosts.table.final': '¿Final?',
  'agentCosts.pager.previous': 'Anterior',
  'agentCosts.pager.next': 'Siguiente',
  'agentCosts.pager.pageOfBilledLines': 'Página {page} de {totalPages} · {rows} líneas facturadas',

  // Gasto por persona
  'agentCosts.users.title': 'Quién consume los créditos',
  'agentCosts.users.description': 'Créditos de Copilot Studio facturados por persona, según Microsoft. Aquí no hay estimaciones ni reparto proporcional, pero procede de un informe de Microsoft distinto del de las cifras por agente anteriores, por lo que los dos totales no siempre coincidirán exactamente. {strong}: el informe por persona de Microsoft no incluye esas dimensiones, por lo que este panel siempre muestra a todos (solo acotado por entorno). No se incluye el gasto de Azure: Azure factura por recurso y nunca registra quién provocó un cargo.',
  'agentCosts.users.descriptionStrong': 'Los filtros de agente, característica, modelo, herramienta y canal no se aplican aquí',
  'agentCosts.users.empty.importOff': 'La importación de créditos de Copilot Studio está desactivada.',
  'agentCosts.users.empty.noUsage': 'No hay consumo de créditos por persona en este periodo.',
  'agentCosts.users.empty.noFigures': 'Aún no hay cifras por persona. Microsoft las agregó a la API de licencias de Power Platform en julio de 2026, por lo que un inquilino cuya API no las ofrezca solo mostrará la vista por agente anterior.',
  'agentCosts.users.unresolvedUser': 'Usuario sin resolver',
  'agentCosts.users.shareCaption': 'Los porcentajes son sobre los {credits} créditos que se muestran aquí, que corresponden a las {people} personas principales; no necesariamente a todas las personas que usaron un agente.',

  // Gasto de Azure
  'agentCosts.azureSpend.description': 'Costes diarios de Microsoft Cost Management para los ámbitos que esta implementación está configurada para leer. Aquí solo se aplica el intervalo de fechas; los filtros de Copilot anteriores no. Azure factura por recurso, por lo que estas cifras no pueden atribuirse a personas concretas.',
  'agentCosts.azureSpend.empty.noCosts': 'No hay costes de Azure almacenados para este periodo.',
  'agentCosts.azureSpend.empty.importOff': 'La importación de costes de Azure está desactivada.',
  'agentCosts.azureSpend.estimate': 'Estimación: aún puede cambiar',
  'agentCosts.azureSpend.final': 'Final: periodo de facturación cerrado',
};

export default agentCosts;
