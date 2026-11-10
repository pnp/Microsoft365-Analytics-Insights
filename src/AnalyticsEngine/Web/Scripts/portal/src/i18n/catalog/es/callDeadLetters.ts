import type { callDeadLetters as en } from '../en/callDeadLetters';

const callDeadLetters: Record<keyof typeof en, string> = {
  'callDeadLetters.title': 'Llamadas de Teams en la cola de mensajes fallidos',
  'callDeadLetters.description':
    'Las notificaciones de llamadas que el importador no pudo importar acaban en la cola de mensajes fallidos de la cola de llamadas de Teams: las que no podrá importar nunca y aquellas cuyos reintentos se agotaron (por ejemplo, después de que Graph o SQL no estuvieran disponibles durante horas). Reprocesar las devuelve a la cola para que el importador vuelva a intentarlo; Vaciar las elimina.',
  'callDeadLetters.ariaLabel': 'Llamadas de Teams en la cola de mensajes fallidos',
  'callDeadLetters.loading': 'Cargando las llamadas en la cola de mensajes fallidos...',
  'callDeadLetters.countLabel': 'Llamadas en la cola de mensajes fallidos',
  'callDeadLetters.countUnavailable': 'Service Bus no devolvió el recuento. La página Estado del servicio explica el motivo.',
  'callDeadLetters.activeLabel': 'En espera en la cola',
  'callDeadLetters.empty': 'La cola de mensajes fallidos está vacía, así que no hay nada que reprocesar ni vaciar.',
  'callDeadLetters.latestLabel': 'Última solicitud',
  'callDeadLetters.latestNone': 'Ninguna por ahora.',
  'callDeadLetters.latestUnavailable':
    'No se pudo leer la última solicitud en Azure Table Storage, así que ahora no se puede hacer otra. Actualice la página dentro de un momento.',

  'callDeadLetters.action.reprocess': 'Reprocesar',
  'callDeadLetters.action.clear': 'Vaciar...',
  'callDeadLetters.action.requesting': 'Avisando al importador...',
  'callDeadLetters.actionName.reprocess': 'Reprocesar',
  'callDeadLetters.actionName.clear': 'Vaciar',
  'callDeadLetters.reprocessHint':
    'Reprocesar devuelve a la cola todas las llamadas que el importador todavía podría importar. Las que no podrá importar nunca y las que entraron en la cola hace más de 30 días (Graph ya no las conserva) se quedan donde están.',
  'callDeadLetters.pendingHint': 'Hay una solicitud en espera o en curso. Podrá hacer otra cuando haya terminado.',

  'callDeadLetters.state.waiting': 'Esperando al importador',
  'callDeadLetters.state.running': 'En curso',
  'callDeadLetters.state.succeeded': 'Terminada',
  'callDeadLetters.state.partial': 'Hecha en parte',
  'callDeadLetters.state.failed': 'Con errores',
  'callDeadLetters.state.notPickedUp': 'Sin recoger',
  'callDeadLetters.state.notFinished': 'Sin terminar',
  'callDeadLetters.state.unknown': 'Estado {state}',

  'callDeadLetters.detail.waiting': 'Solicitada el {time}. El importador busca solicitudes nuevas cada minuto.',
  'callDeadLetters.detail.running':
    'El importador la recogió el {time}. Dedica como máximo {minutes} minutos a cada solicitud y después registra lo que hizo.',
  'callDeadLetters.detail.notPickedUp':
    'Solicitada el {time}, pero ningún importador la recogió en {minutes} minutos, así que no se ejecutará. Compruebe que el WebJob del importador de Office 365 está en ejecución y usa la cuenta de almacenamiento de esta implementación, y vuelva a solicitarlo.',
  'callDeadLetters.detail.notFinished':
    'El importador la recogió el {time}, pero no la terminó en {hours} horas: probablemente se detuvo o se reinició a mitad, así que puede que algunas llamadas ya se hayan tratado. Puede volver a solicitarlo.',
  'callDeadLetters.detail.finished': 'Terminada el {time}.',

  'callDeadLetters.failure.queueUnavailable':
    'El importador no pudo abrir la cola de llamadas de Teams, así que no se hizo nada. La página Estado del servicio indica si se puede acceder a Service Bus.',
  'callDeadLetters.failure.readFailed': 'Falló la lectura de la cola de mensajes fallidos. El registro del importador contiene el error.',
  'callDeadLetters.failure.sendFailures':
    'Devolver llamadas a la cola falló una y otra vez, así que el importador se detuvo. El registro del importador contiene el error.',
  'callDeadLetters.failure.sendFailed':
    'Algunas llamadas no se pudieron devolver a la cola, así que siguen en la cola de mensajes fallidos. Vuelva a solicitarlo para reintentarlas.',
  'callDeadLetters.failure.deleteFailed':
    'Algunas llamadas no se pudieron eliminar, así que siguen en la cola de mensajes fallidos. Vuelva a solicitarlo para reintentarlas.',
  'callDeadLetters.failure.lockedElsewhere':
    'Otro proceso tenía bloqueadas algunas llamadas de la cola de mensajes fallidos (otra reproducción o Service Bus Explorer, por ejemplo), así que el importador no pudo llegar a ellas. Vuelva a solicitarlo cuando estén libres.',
  'callDeadLetters.failure.locksLapsing':
    'Las llamadas que quedaban al principio de la cola de mensajes fallidos perdían sus bloqueos antes de que el importador pudiera pasar de ellas. Vacíe la cola o vuelva a solicitarlo.',
  'callDeadLetters.failure.browseRepeated': 'Algo falló al leer la cola de mensajes fallidos. El registro del importador contiene los detalles.',
  'callDeadLetters.failure.cancelled': 'El importador se detuvo antes de terminar.',
  'callDeadLetters.failure.timeBudget':
    'Se detuvo al cabo de {minutes} minutos, el tiempo máximo de una solicitud, con trabajo pendiente. Vuelva a solicitarlo para continuar.',
  'callDeadLetters.failure.scanLimit':
    'Revisó tantas llamadas de la cola de mensajes fallidos como permite una solicitud sin llegar al final de la cola. Vuelva a solicitarlo para continuar o, si la mayoría de las que quedan no se podrán importar nunca, vacíe la cola.',
  'callDeadLetters.failure.scanLimitClear':
    'Eliminó tantas llamadas de la cola de mensajes fallidos como permite una solicitud, y quedan más. Vuelva a solicitarlo para continuar.',
  'callDeadLetters.failure.unexpectedError': 'Se produjo un error inesperado. El registro del importador contiene el error.',
  'callDeadLetters.failure.unknown': 'No terminó correctamente ({code}). El registro del importador contiene los detalles.',

  'callDeadLetters.result.replayed': 'Devueltas a la cola: {count}',
  'callDeadLetters.result.deleted': 'Eliminadas: {count}',
  'callDeadLetters.result.left': 'Siguen en la cola de mensajes fallidos: {count}',
  'callDeadLetters.result.sendFailed': 'No se pudieron devolver: {count}',
  'callDeadLetters.result.deleteFailed': 'No se pudieron eliminar: {count}',
  'callDeadLetters.result.replayedButNotRemoved':
    '{count} de las llamadas devueltas no se pudieron quitar después de la cola de mensajes fallidos, así que una solicitud posterior podría volver a enviarlas. No pasa nada: al guardar una llamada se reemplaza cualquier copia guardada antes.',
  'callDeadLetters.skipped.neverImportable': 'No se podrán importar nunca tal como están: {count}',
  'callDeadLetters.skipped.callRecordNoLongerInGraph': 'Entraron en la cola hace más de 30 días, así que Graph ya no las conserva: {count}',
  'callDeadLetters.skipped.replayLimitReached': 'Ya se reprocesaron el número máximo de veces: {count}',
  'callDeadLetters.skipped.replayedInThisRun': 'Devueltas por esta solicitud y de nuevo en la cola de mensajes fallidos: {count}',
  'callDeadLetters.skipped.otherReason': 'En la cola por un motivo que esta solicitud no trataba: {count}',
  'callDeadLetters.skipped.unknown': 'Sin tratar por otro motivo ({code}): {count}',

  'callDeadLetters.reasons.title': 'Por motivo de mensaje fallido',
  'callDeadLetters.reasons.ariaLabel': 'Qué pasó con las llamadas de la cola de mensajes fallidos, por motivo',
  'callDeadLetters.reasons.reason': 'Motivo',
  'callDeadLetters.reasons.replayed': 'Devueltas',
  'callDeadLetters.reasons.deleted': 'Eliminadas',
  'callDeadLetters.reasons.left': 'Quedan',
  'callDeadLetters.reason.InvalidCallNotification': 'No es una notificación de llamada de Graph',
  'callDeadLetters.reason.MissingCallId': 'La notificación no tiene id. de llamada',
  'callDeadLetters.reason.CallImportNotRetryable': 'Falló siempre de la misma manera',
  'callDeadLetters.reason.CallImportRetriesExhausted': 'Se agotaron los reintentos',
  'callDeadLetters.reason.CallImportSystemicRetriesExhausted': 'Se agotaron los reintentos durante una interrupción del servicio',
  'callDeadLetters.reason.CallImportRetryWindowExpired': 'Seguía fallando cuando se cerró su plazo de reintentos',
  'callDeadLetters.reason.MaxDeliveryCountExceeded': 'Service Bus desistió tras demasiadas entregas',
  'callDeadLetters.reason.NoOrganiserIdentity': 'El registro de la llamada no indica el organizador',
  'callDeadLetters.reason.NoSessions': 'El registro de la llamada no tiene sesiones',
  'callDeadLetters.reason.NoUsableSessions': 'No se pudo usar ninguna de las sesiones del registro de la llamada',
  'callDeadLetters.reason.NoCallType': 'El registro de la llamada no tiene tipo de llamada',
  'callDeadLetters.reason.none': 'Sin motivo registrado',

  'callDeadLetters.unavailable.callsImportDisabled': 'La importación de llamadas de Teams está desactivada, así que no hay cola de llamadas con la que trabajar.',
  'callDeadLetters.unavailable.serviceBusNotConfigured': 'Service Bus no está configurado en esta implementación, así que no hay cola de llamadas de Teams.',
  'callDeadLetters.unavailable.stateStoreInMemory':
    'Esta implementación no tiene cadena de conexión de Azure Storage, así que el portal no puede pasar una solicitud al importador, que se ejecuta como un WebJob aparte. Un operador puede reprocesar o vaciar igualmente la cola de mensajes fallidos ejecutando el importador con --replayCallDeadLetters desde la consola del WebJob.',
  'callDeadLetters.unavailable.unknown': 'No está disponible en esta implementación ({reason}).',

  'callDeadLetters.error.loadFailed': 'No se pudieron cargar las llamadas de la cola de mensajes fallidos ({status}).',
  'callDeadLetters.error.loadFailedGeneric': 'No se pudieron cargar las llamadas de la cola de mensajes fallidos.',
  'callDeadLetters.error.requestFailed': 'No se pudo pasar la solicitud al importador ({status}).',
  'callDeadLetters.error.requestFailedGeneric': 'No se pudo pasar la solicitud al importador.',
  'callDeadLetters.error.unknownAction': 'El servidor no reconoció la solicitud. Vuelva a cargar la página e inténtelo de nuevo.',
  'callDeadLetters.error.requestPending':
    'Ya hay otra solicitud en espera o en curso. Podrá hacer otra cuando haya terminado o cuando haya caducado.',
  'callDeadLetters.error.stateStoreUnavailable':
    'No se pudo acceder a Azure Table Storage, donde se guardan las solicitudes. Inténtelo de nuevo dentro de un momento.',

  'callDeadLetters.confirm.title': '¿Vaciar la cola de mensajes fallidos?',
  'callDeadLetters.confirm.body':
    'Se eliminarán para siempre todas las notificaciones de llamadas de Teams de la cola de mensajes fallidos, sea cual sea su motivo o su antigüedad, incluidas las que el importador todavía podría importar. Esas llamadas no se importarán nunca y no se puede deshacer.',
  'callDeadLetters.confirm.count': 'En el último recuento había {count}.',
  'callDeadLetters.confirm.alternative': 'Si prefiere que el importador vuelva a intentarlo, use Reprocesar.',
  'callDeadLetters.confirm.clear': 'Eliminarlas para siempre',
};

export default callDeadLetters;
