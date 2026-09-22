import type en from '../en/userOrgs';

/**
 * Spanish text for the User organisations admin page.
 *
 * Typed against its English counterpart, so a key added there and forgotten here is a build error
 * rather than an English sentence appearing mid-page for a Spanish reader.
 */
export const userOrgs: Record<keyof typeof en, string> = {
  // Page shell
  'userOrgs.page.title': 'Organizaciones de usuario',
  'userOrgs.page.new': 'Nuevo tipo de organizaci\u00f3n',
  'userOrgs.page.intro':
    'Agrupe a los usuarios por criterios que su directorio no mantiene de forma fiable: un centro de coste, una unidad de negocio o un equipo procedente de una exportaci\u00f3n de RR. HH. Cada tipo obtiene sus valores de un atributo personalizado de Microsoft Entra, le\u00eddo en cada importaci\u00f3n de usuarios, o de un archivo CSV que cargue aqu\u00ed. Cada usuario tiene como m\u00e1ximo un valor por tipo.',
  'userOrgs.page.loading': 'Cargando tipos de organizaci\u00f3n...',

  // Types table
  'userOrgs.types.title': 'Tipos de organizaci\u00f3n',
  'userOrgs.types.empty': 'A\u00fan no hay ninguno definido. Cree uno para empezar a agrupar usuarios.',
  'userOrgs.column.name': 'Nombre',
  'userOrgs.column.source': 'Origen',
  'userOrgs.column.usersAssigned': 'Usuarios asignados',
  'userOrgs.column.distinctValues': 'Valores distintos',
  'userOrgs.column.state': 'Estado',
  'userOrgs.column.actions': 'Acciones',
  'userOrgs.source.entra': 'Atributo de Entra',
  'userOrgs.source.csv': 'Carga de CSV',
  'userOrgs.source.lastImported': '\u00faltima importaci\u00f3n {when} por {who} ({status})',
  'userOrgs.source.unknownUser': 'desconocido',
  'userOrgs.state.enabled': 'Habilitado',
  'userOrgs.state.disabled': 'Deshabilitado',
  'userOrgs.action.edit': 'Editar',
  'userOrgs.action.delete': 'Eliminar',

  // Job status, as shown beside a CSV type
  'userOrgs.status.pending': 'pendiente',
  'userOrgs.status.running': 'en curso',
  'userOrgs.status.succeeded': 'completada',
  'userOrgs.status.failed': 'con errores',
  'userOrgs.status.cancelled': 'cancelada',
  'userOrgs.status.interrupted': 'interrumpida',

  // Toasts and confirmations
  'userOrgs.toast.created': 'Se ha creado {name}.',
  'userOrgs.toast.saved': 'Se ha guardado {name}.',
  'userOrgs.toast.deleted': 'Se ha eliminado {name}.',
  'userOrgs.delete.confirm':
    '\u00bfEliminar "{name}"?\n\nEsto quita los valores de organizaci\u00f3n de {count} usuario(s) y no se puede deshacer.',

  // Entra explainer card
  'userOrgs.entraCard.title': 'C\u00f3mo se mantienen actualizados los tipos basados en Entra',
  'userOrgs.entraCard.body':
    'Estos se leen durante la importaci\u00f3n normal de usuarios, por lo que los valores aparecen tras el siguiente ciclo de importaci\u00f3n. Cualquier cambio en los atributos de Entra en uso (a\u00f1adir o eliminar un tipo, habilitarlo o deshabilitarlo, apuntarlo a otro atributo o cambiarlo a CSV) hace que el siguiente ciclo vuelva a leer a todos los usuarios una vez, de modo que el nuevo conjunto se rellene tambi\u00e9n para quienes no hayan cambiado por otro motivo. Ese ciclo tarda m\u00e1s de lo habitual y, en un inquilino grande, de forma notable.',

  // CSV import card
  'userOrgs.import.cardTitle': 'Importar {name} desde un archivo',
  'userOrgs.import.cardIntro':
    'Un CSV con una columna de usuario y una columna de organizaci\u00f3n, en cualquier orden. Una fila con la organizaci\u00f3n en blanco borra el valor de ese usuario.',

  // Create / edit dialog
  'userOrgs.dialog.editTitle': 'Editar {name}',
  'userOrgs.dialog.newTitle': 'Nuevo tipo de organizaci\u00f3n',
  'userOrgs.dialog.nameLabel': 'Nombre',
  'userOrgs.dialog.nameHint':
    'La etiqueta con la que se muestra esta agrupaci\u00f3n en la p\u00e1gina de consulta de usuarios, por ejemplo Centro de coste. Los tipos de organizaci\u00f3n todav\u00eda no est\u00e1n disponibles como filtro en los informes.',
  'userOrgs.dialog.sourceLabel': 'De d\u00f3nde proceden los valores',
  'userOrgs.dialog.sourceEntra':
    'Un atributo personalizado de Microsoft Entra, le\u00eddo en cada importaci\u00f3n de usuarios',
  'userOrgs.dialog.sourceCsv': 'Un archivo CSV cargado aqu\u00ed',
  'userOrgs.dialog.discardWarning.one':
    'Al guardar se descarta el {count} valor que este tipo tiene hoy. Se ley\u00f3 de un origen que dejar\u00e1 de ser la fuente de verdad, por lo que mantenerlo mostrar\u00eda un valor obsoleto de forma indefinida: una combinaci\u00f3n (Merge) de CSV nunca toca a los usuarios que el archivo no menciona.',
  'userOrgs.dialog.discardWarning.other':
    'Al guardar se descartan los {count} valores que este tipo tiene hoy. Se leyeron de un origen que dejar\u00e1 de ser la fuente de verdad, por lo que mantenerlos mostrar\u00eda valores obsoletos de forma indefinida: una combinaci\u00f3n (Merge) de CSV nunca toca a los usuarios que el archivo no menciona.',
  'userOrgs.dialog.attributeLabel': 'Atributo de Entra',
  'userOrgs.dialog.attributeHint':
    'Uno de extensionAttribute1-15, employeeId, employeeType, employeeOrgData.costCenter, employeeOrgData.division, una extensi\u00f3n de directorio (extension_APPID_NAME, con el id. de aplicaci\u00f3n y el nombre de la extensi\u00f3n) o una extensi\u00f3n de esquema.',
  'userOrgs.dialog.testLabel': 'Pru\u00e9belo con un usuario',
  'userOrgs.dialog.testHint':
    'Obligatorio antes de guardar. Microsoft Graph rechaza toda la importaci\u00f3n de usuarios si no reconoce el atributo, as\u00ed que hay que comprobarlo primero.',
  'userOrgs.dialog.testButton': 'Probar',
  'userOrgs.dialog.testing': 'Probando...',
  'userOrgs.dialog.testFirst': 'Pruebe el atributo con un usuario antes de guardar.',
  'userOrgs.dialog.enabled': 'Habilitado: se incluye en las importaciones',
  'userOrgs.dialog.disabled': 'Deshabilitado: no se importa',
  'userOrgs.dialog.cancel': 'Cancelar',
  'userOrgs.dialog.save': 'Guardar',
  'userOrgs.dialog.saving': 'Guardando...',

  // Test outcome
  'userOrgs.test.failed': 'No se ha podido leer el atributo.',
  'userOrgs.test.succeeded': 'El atributo se ha le\u00eddo correctamente.',
  'userOrgs.test.graphProperty': 'Propiedad de Graph',
  'userOrgs.test.rawValue': 'Valor de Graph',
  'userOrgs.test.storedAs': 'Se almacena como',

  // CSV import panel - controls
  'userOrgs.csv.clear': 'Quitar',
  'userOrgs.csv.modeLabel': '\u00bfQu\u00e9 debe ocurrir con los usuarios que no est\u00e1n en el archivo?',
  'userOrgs.csv.modeMerge': 'Combinar: dejarlos exactamente como est\u00e1n',
  'userOrgs.csv.modeReplace':
    'Reemplazar: borrar su valor de {name} (el archivo es la lista completa)',
  'userOrgs.csv.import.one': 'Importar {count} fila',
  'userOrgs.csv.import.other': 'Importar {count} filas',

  // Replace warning
  'userOrgs.csv.clearWarning.one': 'Esto borrar\u00e1 el valor de {name} de 1 usuario.',
  'userOrgs.csv.clearWarning.other': 'Esto borrar\u00e1 el valor de {name} de {count} usuarios.',
  'userOrgs.csv.clearWarning.keeps':
    'El archivo conserva {kept} de los {assigned} usuarios que hoy tienen uno. Quien no aparezca en \u00e9l perder\u00e1 el suyo.',
  'userOrgs.csv.clearWarning.unknown.one':
    '1 fila del archivo no coincide con ning\u00fan usuario: si no lo esperaba, revise el archivo antes de continuar.',
  'userOrgs.csv.clearWarning.unknown.other':
    '{count} filas del archivo no coinciden con ning\u00fan usuario: si no lo esperaba, revise el archivo antes de continuar.',
  'userOrgs.csv.confirmClear': 'Lo entiendo: borrar los usuarios que este archivo no incluye',

  // Preview
  'userOrgs.csv.headerFound':
    'Fila de encabezado detectada: "{upnColumn}" y "{orgColumn}", separadas por {delimiter}.',
  'userOrgs.csv.headerMissing':
    'No se ha reconocido ninguna fila de encabezado, por lo que la primera columna se trata como el usuario y la segunda como la organizaci\u00f3n. Separadas por {delimiter}.',
  'userOrgs.csv.matchSummary':
    '{matched} de las {total} filas del archivo coinciden con un usuario de esta base de datos.',
  'userOrgs.csv.unknownRows.one':
    '1 fila del archivo no coincide con ning\u00fan usuario de esta base de datos y se omitir\u00e1. Compruebe que el archivo usa los mismos nombres principales de usuario que importa el producto y que no es una exportaci\u00f3n parcial.',
  'userOrgs.csv.unknownRows.other':
    '{count} filas del archivo no coinciden con ning\u00fan usuario de esta base de datos y se omitir\u00e1n. Compruebe que el archivo usa los mismos nombres principales de usuario que importa el producto y que no es una exportaci\u00f3n parcial.',
  'userOrgs.csv.previewAriaLabel': 'Vista previa del archivo',
  'userOrgs.csv.column.line': 'L\u00ednea',
  'userOrgs.csv.column.user': 'Usuario',
  'userOrgs.csv.column.organisation': 'Organizaci\u00f3n',
  'userOrgs.csv.column.matches': 'Coincide con un usuario',
  'userOrgs.csv.clearsValue': 'borra el valor',
  'userOrgs.csv.showingFirst':
    'Se muestran las primeras {count} filas. Se importa el archivo completo.',
  'userOrgs.csv.truncated.one':
    '1 nombre de organizaci\u00f3n supera los 200 caracteres y se almacenar\u00e1 abreviado. Los nombres id\u00e9nticos en sus primeros 200 caracteres se convierten en una sola organizaci\u00f3n.',
  'userOrgs.csv.truncated.other':
    '{count} nombres de organizaci\u00f3n superan los 200 caracteres y se almacenar\u00e1n abreviados. Los nombres id\u00e9nticos en sus primeros 200 caracteres se convierten en una sola organizaci\u00f3n.',
  'userOrgs.csv.problems':
    'Algunas filas no se pueden usar: {problems}. Se omiten y se contabilizan; el resto del archivo se importa igualmente.',
  'userOrgs.csv.problemLine': 'l\u00ednea {line} ({reason})',

  // Job progress
  'userOrgs.job.importing':
    'Importando {count} fila(s)... esta p\u00e1gina se actualizar\u00e1 cuando termine.',
  'userOrgs.job.interrupted':
    'Esta importaci\u00f3n dej\u00f3 de informar de su progreso, lo que suele significar que la aplicaci\u00f3n web se reinici\u00f3 mientras se ejecutaba. Se aplic\u00f3 por completo o no se aplic\u00f3 en absoluto (el archivo se aplica en una \u00fanica transacci\u00f3n, as\u00ed que no puede quedar a medias), pero no se ha registrado cu\u00e1l de las dos. Vuelva a cargar el archivo para asegurarse; importar el mismo archivo dos veces es inocuo.',
  'userOrgs.job.failed': 'La importaci\u00f3n ha {status}. {message}',
  'userOrgs.job.finished': 'Importaci\u00f3n finalizada.',
  'userOrgs.job.changed': '{count} modificados',
  'userOrgs.job.cleared': '{count} borrados',
  'userOrgs.job.unknownUsers': '{count} usuario(s) desconocido(s)',
  'userOrgs.job.unusableRows': '{count} fila(s) inutilizable(s)',
};

export default userOrgs;
