import type en from '../en/globalFilter';

const globalFilter: Record<keyof typeof en, string> = {
  'globalFilter.bar.label': 'Establecido por su administrador',
  'globalFilter.bar.groupAria': 'Filtro establecido por un administrador del portal',
  'globalFilter.bar.info':
    'Un administrador del portal estableció este filtro. Cada informe de Análisis muestra solo a las personas que lo cumplen, además de cualquier filtro que usted añada, y no puede cambiarlo ni quitarlo. Mientras se aplica, no se cuenta la actividad que no se puede vincular a una persona del directorio.',
  'globalFilter.bar.infoAria': 'Acerca de este filtro',
  'globalFilter.bar.coverage': '{matched} de {total} personas',
  'globalFilter.bar.readback': 'Se muestran las personas que cumplen: {description}.',
  'globalFilter.bar.edit': 'Editar',
  'globalFilter.bar.switchOff': 'Desactivar en mi vista',
  'globalFilter.bar.switchOffHint':
    'Vea todos los informes sin este filtro. Solo cambia su propia vista: los demás siguen viendo los informes filtrados.',
  'globalFilter.bar.switchOn': 'Volver a activar',
  'globalFilter.bar.switching': 'Cambiando…',
  'globalFilter.bar.bypassed':
    'El filtro que estableció un administrador del portal está desactivado en su vista, por lo que estos informes incluyen a todos. Los demás siguen viendo solo a las personas que lo cumplen.',
  'globalFilter.bar.invalid':
    'Esta versión del portal no puede leer el filtro que estableció un administrador del portal, por lo que los informes no están disponibles hasta que un administrador lo corrija.',
  'globalFilter.bar.invalidAdmin':
    'Esta versión del portal no puede leer el filtro global de informes, por lo que los informes se rechazan para todos hasta que se sustituya.',
  'globalFilter.bar.loadFailed':
    'No se pudo comprobar si un administrador del portal ha establecido un filtro. Si hay uno, los informes lo siguen aplicando.',
  'globalFilter.bar.retry': 'Reintentar',
  'globalFilter.bar.viewerNotFound':
    'Algunas de estas condiciones dependen de sus propios datos y usted no figura en el directorio de usuarios que usan los informes, por lo que no las cumple nadie. Los informes no mostrarán a nadie hasta que se importe su cuenta.',
  'globalFilter.bar.matchesNobody': 'Este filtro no lo cumple nadie en su caso, por lo que los informes estarán vacíos.',
  'globalFilter.bar.unknownDimension':
    'Una condición hace referencia a un tipo de organización que ya no existe, por lo que esa condición no la cumple nadie.',

  'globalFilter.note.overview':
    'Se aplica a los informes de Análisis. Los recuentos de esta página describen todos los datos que contiene el servicio.',
  'globalFilter.note.teams':
    'Las cifras de los equipos (colaboración y conversaciones) incluyen todos los equipos, porque un equipo no es una persona que el filtro pueda seleccionar.',
  'globalFilter.note.agentCosts':
    'Reduce la lista de personas. Las cifras de costes de agentes y de Azure abarcan toda la organización, porque pertenecen a los agentes y no a las personas.',

  'globalFilter.pill.lockedAria': '{condition}. Establecido por un administrador del portal.',
  'globalFilter.pill.unresolved': 'no consta para usted',

  'globalFilter.viewer.option.own': 'Valor propio del lector',
  'globalFilter.viewer.option.self': 'El propio lector',
  'globalFilter.viewer.option.manager': 'El responsable del lector',
  'globalFilter.viewer.phrase.own': 'el valor propio del lector',
  'globalFilter.viewer.phrase.self': 'la persona lectora',
  'globalFilter.viewer.phrase.manager': 'la persona responsable del lector',
  'globalFilter.viewer.hint.own':
    'Elija «Valor propio del lector» para mostrar a cada lector las personas que comparten el suyo: a cada responsable su propio departamento, por ejemplo.',
  'globalFilter.viewer.hint.userName': 'Elija «El propio lector» para mostrar a cada lector solo sus propias cifras.',
  'globalFilter.viewer.hint.manager':
    '«El propio lector» muestra a cada lector sus subordinados directos. «El responsable del lector» le muestra a todos los que comparten su responsable.',
  'globalFilter.viewer.hint.managementChain':
    '«El propio lector» muestra a cada lector a todos los que dependen de él, en cualquier nivel. «El responsable del lector» muestra toda la organización de su responsable.',

  'globalFilter.reader.own': '{value} (según su perfil)',
  'globalFilter.reader.self': '{value} (usted)',
  'globalFilter.reader.manager': '{value} (su responsable)',
  'globalFilter.reader.unresolved.own':
    '{dimension} debe coincidir con el suyo, que no consta para usted, por lo que esta condición no la cumple nadie',
  'globalFilter.reader.unresolved.self':
    '{dimension} debe coincidir con usted, pero no figura en el directorio que usan los informes, por lo que esta condición no la cumple nadie',
  'globalFilter.reader.unresolved.manager':
    '{dimension} debe coincidir con su responsable, que no consta para usted, por lo que esta condición no la cumple nadie',

  'globalFilter.print.heading': 'Filtro establecido por un administrador del portal',
  'globalFilter.print.description': 'Solo las personas que cumplen: {description}.',
  'globalFilter.print.coverage': '{matched} de las {total} personas del directorio lo cumplen para la persona que consulta.',
  'globalFilter.print.bypassed':
    'El filtro de informes de un administrador del portal estaba desactivado en esta vista, por lo que no reduce estas cifras.',

  'globalFilter.banner.setByAdmin': '{description} (establecido por un administrador del portal)',

  'globalFilter.editor.groupAria': 'Condiciones del filtro global',
  'globalFilter.editor.none': 'Sin condiciones: los informes incluyen a todos, salvo por el filtro que añada cada lector.',
  'globalFilter.editor.add': 'Añadir condición',
  'globalFilter.editor.readback': 'Los lectores solo ven a las personas que cumplen: {description}.',
  'globalFilter.editor.problem.tooManyClauses': 'Un filtro puede tener como máximo {max} condiciones.',
  'globalFilter.editor.problem.valueTooLong': 'Uno de los valores es demasiado largo para filtrar por él.',
  'globalFilter.editor.problem.tooLong': 'El filtro es demasiado largo para guardarlo. Quite algunos valores o condiciones.',
  'globalFilter.editor.problem.viewerText':
    'Una condición que busca texto no puede compararse también con el valor propio del lector.',

  'globalFilter.admin.title': 'Filtro global de informes',
  'globalFilter.admin.intro':
    'Condiciones que cada informe de Análisis aplica a todos los que lo abren, además de cualquier filtro que añadan ellos mismos. Los lectores ven estas condiciones en cada página, pero no pueden cambiarlas ni quitarlas. Una condición puede compararse con el valor propio del lector, de modo que un solo filtro puede mostrar a cada responsable su propio departamento.',
  'globalFilter.admin.loading': 'Cargando el filtro global…',
  'globalFilter.admin.loadFailed': 'No se pudo cargar el filtro global.',
  'globalFilter.admin.retry': 'Reintentar',
  'globalFilter.admin.reload': 'Recargar',
  'globalFilter.admin.rolesNotEnforced':
    'Los roles del portal no se aplican en esta implementación, por lo que cualquiera que inicie sesión es administrador del portal: cualquiera puede cambiar este filtro o desactivarlo en su propia vista. Active la aplicación de roles (EnforcePortalRoles) antes de confiar en él para limitar lo que ve cada persona.',
  'globalFilter.admin.storageUnavailable':
    'La base de datos no se ha actualizado para guardar un filtro global, por lo que no se puede guardar ninguno. Ejecute el instalador, o el script de actualización manual {script}, y recargue esta página.',
  'globalFilter.admin.invalidStored':
    'Esta versión del portal no puede leer el filtro guardado, por lo que los informes de Análisis se rechazan para todos. Guarde uno nuevo, o guarde sin condiciones para quitarlo.',
  'globalFilter.admin.bypassedForYou':
    'El filtro está desactivado en su propia vista. Vuelva a activarlo desde la barra de filtro de cualquier página de Análisis.',
  'globalFilter.admin.conditions.heading': 'Condiciones',
  'globalFilter.admin.conditions.note':
    'Las personas deben cumplir estas condiciones para aparecer en cualquier informe. Las condiciones unidas por Y deben cumplirse todas; O inicia otro grupo, y basta con cumplir uno de los grupos.',
  'globalFilter.admin.preview.heading': 'Lo que vería usted',
  'globalFilter.admin.preview.note':
    'El filtro también se aplica a los administradores del portal, así que esto es lo que mostrarían sus propios informes una vez guardado.',
  'globalFilter.admin.preview.none': 'Sin condiciones, los informes incluirían a todos.',
  'globalFilter.admin.preview.loading': 'Comprobando…',
  'globalFilter.admin.preview.failed': 'No se pudo obtener la vista previa de este filtro.',
  'globalFilter.admin.preview.coverage': 'Vería a {matched} de las {total} personas del directorio.',
  'globalFilter.admin.preview.description': 'En su caso, eso significa: {description}.',
  'globalFilter.admin.preview.viewerNotFound':
    'Usted no figura en el directorio de usuarios que usan los informes, por lo que las condiciones sobre sus propios datos no las cumple nadie en su caso. Siguen funcionando para los lectores que sí figuran.',
  'globalFilter.admin.save': 'Guardar',
  'globalFilter.admin.saving': 'Guardando…',
  'globalFilter.admin.discard': 'Descartar cambios',
  'globalFilter.admin.unsaved': 'Cambios sin guardar',
  'globalFilter.admin.saveFailed': 'No se pudo guardar el filtro global.',
  'globalFilter.admin.neverSet': 'Todavía no se ha guardado ningún filtro global.',
  'globalFilter.admin.lastChanged': 'Último cambio: {when}.',
  'globalFilter.admin.lastChangedBy': 'Último cambio: {when}, por {by}.',
  'globalFilter.admin.propagation':
    'Un cambio guardado se aplica de inmediato en este servidor, y en las demás instancias de la aplicación web en un minuto.',
  'globalFilter.admin.toast.saved': 'Filtro global guardado. Todos los informes lo aplican a partir de ahora.',
  'globalFilter.admin.toast.removed': 'Filtro global quitado. Los informes vuelven a incluir a todos.',
};

export default globalFilter;
