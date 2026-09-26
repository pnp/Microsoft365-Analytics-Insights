import type { common as en } from '../en/common';

/**
 * Spanish (es-ES) text shared across the whole portal.
 *
 * Typed against the English module, so a key added there without a translation here fails the
 * build rather than reaching a customer as English text inside a Spanish page.
 */
const common: Record<keyof typeof en, string> = {
  // Acciones
  'common.action.apply': 'Aplicar',
  'common.action.cancel': 'Cancelar',
  'common.action.clear': 'Borrar',
  'common.action.close': 'Cerrar',
  'common.action.copyToClipboard': 'Copiar al portapapeles',
  'common.action.export': 'Exportar',
  'common.action.print': 'Imprimir',
  'common.action.refresh': 'Actualizar',
  'common.action.retry': 'Reintentar',
  'common.action.search': 'Buscar',
  'common.action.showAll': 'Mostrar todo',
  'common.action.showLess': 'Mostrar menos',
  'common.action.showMore': 'Mostrar m\u00e1s',

  // Estados
  'common.state.loading': 'Cargando\u2026',
  'common.state.noData': 'Sin datos',
  'common.state.notAvailable': 'No disponible',
  'common.state.notReported': 'No informado',
  'common.state.none': 'Ninguno',
  'common.state.unknown': 'Desconocido',
  'common.state.error': 'Se ha producido un error',
  'common.state.on': 'activado',
  'common.state.off': 'desactivado',
  'common.state.yes': 'S\u00ed',
  'common.state.no': 'No',
  'common.state.enabled': 'Habilitado',
  'common.state.disabled': 'Deshabilitado',

  // Unidades y recuentos
  'common.unit.user.one': '{count} usuario',
  'common.unit.user.other': '{count} usuarios',
  'common.unit.day.one': '{count} d\u00eda',
  'common.unit.day.other': '{count} d\u00edas',
  'common.unit.users': 'usuarios',
  'common.unit.days': 'd\u00edas',

  // Tiempo relativo
  'common.time.today': 'hoy',
  'common.time.yesterday': 'ayer',
  'common.time.daysAgo': 'hace {days} d\u00edas',

  // InfoTip
  'common.infoTip.ariaLabel': 'C\u00f3mo se calcula \u00ab{title}\u00bb',
  'common.infoTip.calculation': 'C\u00e1lculo',

  // Avisos de datos descartables
  'common.warnings.show.one': 'Mostrar 1 aviso sobre los datos',
  'common.warnings.show.other': 'Mostrar {count} avisos sobre los datos',
  'common.warnings.hide': 'Ocultar estos avisos',

  // Bot\u00f3n Imprimir
  'common.print.preparing': 'Preparando\u2026',
  'common.print.tooManyRows.title': 'Demasiadas filas para imprimir',
  'common.print.tooManyRows.body':
    '{rows} filas coinciden con los filtros de esta lista, y una lista solo se imprime completa hasta {limit} filas. Acote la lista con sus filtros y vuelva a imprimir, o exp\u00f3rtela para obtener todas las filas.',
  'common.print.failed.title': 'No se ha podido preparar la impresi\u00f3n',
  'common.print.failed.body':
    'No se ha podido cargar la lista completa para imprimirla, as\u00ed que no se ha impreso nada. Vuelva a intentarlo dentro de un momento.',

  // Ventana emergente de SQL
  'common.sql.title': 'SQL para reproducir este dato',
  'common.sql.buttonLabel': 'SQL',
  'common.sql.copied': 'SQL copiado al portapapeles',
  'common.sql.copyFailed': 'No se ha podido copiar al portapapeles',

  // Sem\u00e1foro de sentimiento
  'common.sentiment.notScored': 'Sin puntuaci\u00f3n para este periodo. {note}',
  'common.sentiment.detail': 'Sentimiento {score} ({band}). {note}',
  'common.sentiment.scaleNote': 'El sentimiento va de 0 (negativo) a 1 (positivo), ponderado por recuento de mensajes, y 0,5 es neutro. No es un porcentaje de mensajes positivos.',
  'common.sentiment.band.negative': 'negativo',
  'common.sentiment.band.leaningNegative': 'tendencia negativa',
  'common.sentiment.band.neutral': 'neutro',
  'common.sentiment.band.leaningPositive': 'tendencia positiva',
  'common.sentiment.band.positive': 'positivo',

  'common.serverPlaceholder.noDepartment': '(sin departamento)',
  'common.serverPlaceholder.noDepartmentCapitalised': '(Sin departamento)',
  'common.serverPlaceholder.noCountry': '(sin país)',
  'common.serverPlaceholder.noOffice': '(sin oficina)',
  'common.serverPlaceholder.noCompany': '(sin empresa)',
  'common.serverPlaceholder.noManager': '(sin responsable)',
  'common.serverPlaceholder.noDomain': '(sin dominio)',
  'common.serverPlaceholder.noDomainCapitalised': '(Sin dominio)',
  'common.serverPlaceholder.noReasonRecorded': '(sin motivo registrado)',
  'common.serverPlaceholder.unknown': '(desconocido)',
  'common.serverPlaceholder.unknownSite': '(sitio desconocido)',
  'common.serverPlaceholder.otherSites': '(otros sitios)',
  'common.serverPlaceholder.unknownCountry': '(país desconocido)',
  'common.serverPlaceholder.otherCountries': '(otros países)',
  'common.serverPlaceholder.unknownDevice': '(dispositivo desconocido)',
  'common.serverPlaceholder.otherDevices': '(otros dispositivos)',
  'common.serverPlaceholder.untitledElement': '(elemento sin título)',
  'common.serverPlaceholder.unnamedAgent': '(agente sin nombre)',
  'common.serverPlaceholder.notSet': '(sin establecer)',
  'common.serverPlaceholder.notStated': '(sin indicar)',
  'common.serverPlaceholder.none': '(ninguno)',
  'common.serverPlaceholder.disabled': '(deshabilitado)',
  'common.serverPlaceholder.redisNotConfigured': '(no configurado: análisis profundos de Teams deshabilitados)',
};

export default common;
