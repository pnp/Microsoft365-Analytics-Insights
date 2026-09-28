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
  'userOrgs.column.lastRefreshed': '\u00daltima actualizaci\u00f3n',
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
  'userOrgs.types.viewUsers.one': 'Ver el {count} usuario de {name}',
  'userOrgs.types.viewUsers.other': 'Ver los {count} usuarios de {name}',

  // "Last refreshed" for a type that never has been, by what it is waiting for
  'userOrgs.lastRefreshed.waitingForImport': 'Pendiente de la pr\u00f3xima importaci\u00f3n de usuarios',
  'userOrgs.lastRefreshed.noImportYet': 'A\u00fan no hay ninguna importaci\u00f3n correcta',
  'userOrgs.lastRefreshed.clearedBySourceChange': 'Se borr\u00f3 al cambiar el origen',
  'userOrgs.lastRefreshed.never': 'Nunca',

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
  'userOrgs.entraCard.lastRefreshed':
    'La columna \u00daltima actualizaci\u00f3n indica cu\u00e1ndo una importaci\u00f3n de usuarios ley\u00f3 por \u00faltima vez el atributo y aplic\u00f3 sus valores. De forma predeterminada ocurre en cada ciclo del importador, y aproximadamente una vez al d\u00eda si ImportAggressiveness tiene el valor Balanced o Gentle; un tipo guardado mientras ya se est\u00e1 ejecutando una importaci\u00f3n espera a la siguiente. Si todos los tipos de Entra dejan de avanzar a la vez, lo m\u00e1s probable es que Microsoft Graph est\u00e9 rechazando uno de los atributos: en ese caso, la importaci\u00f3n de usuarios contin\u00faa sin ninguno de ellos. Pruebe cada tipo para localizar el que falla o revise el registro del importador.',

  // CSV import card
  'userOrgs.import.cardTitle': 'Importar {name} desde un archivo',
  'userOrgs.import.cardIntro':
    'Un CSV con una columna de usuario y una columna de organizaci\u00f3n, en cualquier orden. Una fila con la organizaci\u00f3n en blanco borra el valor de ese usuario.',

  // What an uploaded file looks like - shown when a CSV type is created, and beside its upload
  'userOrgs.csvFormat.title': 'Qu\u00e9 aspecto debe tener el archivo',
  'userOrgs.csvFormat.intro':
    'Dos columnas: el nombre principal de usuario de cada persona y el valor que tiene para este tipo. Por ejemplo:',
  'userOrgs.csvFormat.defaultColumn': 'Organizaci\u00f3n',
  'userOrgs.csvFormat.example':
    'UserPrincipalName,{column}\nalex.wilber@contoso.com,Finanzas\nmegan.bowen@contoso.com,Investigaci\u00f3n y desarrollo\nadele.vance@contoso.com,',
  'userOrgs.csvFormat.exampleAria': 'Archivo de ejemplo',
  'userOrgs.csvFormat.ruleHeader':
    'La fila de encabezado es opcional y las dos columnas pueden ir en cualquier orden. La columna de usuario se reconoce por un encabezado como UserPrincipalName, UPN, User o Email (en ingl\u00e9s); si el archivo tiene una columna de correo electr\u00f3nico y otra UserPrincipalName o UPN, use la de UPN. Si no hay fila de encabezado, la primera columna se toma como el usuario y la segunda como el valor.',
  'userOrgs.csvFormat.ruleSeparator':
    'Separe las columnas con comas, puntos y comas, tabulaciones o barras verticales: el separador se detecta autom\u00e1ticamente. Guarde el archivo como UTF-8 (\u00abCSV UTF-8\u00bb en Excel). Se rechazan otras codificaciones porque los nombres con acentos o en alfabetos no latinos se corromper\u00edan.',
  'userOrgs.csvFormat.ruleBlank':
    'Una fila sin valor, como la \u00faltima del ejemplo, borra el valor de esa persona.',
  'userOrgs.csvFormat.ruleUsers':
    'Use los nombres principales de usuario que este producto importa de Microsoft Entra. Antes de importar nada, una vista previa muestra cu\u00e1ntas filas coinciden con un usuario; las filas que no coinciden con nadie se omiten.',
  'userOrgs.csvFormat.ruleColumns':
    'Asigne a la columna de valor el nombre de este tipo de organizaci\u00f3n, como en el ejemplo. Si el archivo tiene m\u00e1s columnas, se le pedir\u00e1 que indique cu\u00e1l contiene el nombre principal de usuario y cu\u00e1l contiene el valor.',

  // Create / edit dialog
  'userOrgs.dialog.editTitle': 'Editar {name}',
  'userOrgs.dialog.newTitle': 'Nuevo tipo de organizaci\u00f3n',
  'userOrgs.dialog.nameLabel': 'Nombre',
  'userOrgs.dialog.nameHint':
    'La etiqueta con la que se muestra esta agrupaci\u00f3n: en la p\u00e1gina de consulta de usuarios y como propiedad en el filtro del informe Adopci\u00f3n de Copilot. Por ejemplo, Centro de coste.',
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
  'userOrgs.csv.fileLabel': 'Elegir un archivo CSV',
  'userOrgs.csv.clear': 'Quitar',
  'userOrgs.csv.previewing': 'Leyendo el archivo y comprobando todos los usuarios...',
  'userOrgs.csv.modeLabel': '\u00bfQu\u00e9 debe ocurrir con los usuarios que no est\u00e1n en el archivo?',
  'userOrgs.csv.modeMerge': 'Combinar: dejarlos exactamente como est\u00e1n',
  'userOrgs.csv.modeReplace':
    'Reemplazar: borrar su valor de {name} (el archivo es la lista completa)',
  'userOrgs.csv.import.one': 'Importar {count} fila',
  'userOrgs.csv.import.other': 'Importar {count} filas',
  'userOrgs.csv.columnChooser.hint': 'Este archivo tiene {count} columnas. Elija qu\u00e9 columnas importar.',
  'userOrgs.csv.columnChooser.user': 'Columna del nombre principal de usuario',
  'userOrgs.csv.columnChooser.value': 'Columna de valor',
  'userOrgs.csv.columnChooser.fallback': 'Columna {number}',
  'userOrgs.csv.columnChooser.placeholder': 'Elija una columna',

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
  'userOrgs.csv.mergeClearWarning.one':
    'Esto borrar\u00e1 el valor de {name} de 1 usuario: el archivo lo incluye con un valor vac\u00edo.',
  'userOrgs.csv.mergeClearWarning.other':
    'Esto borrar\u00e1 el valor de {name} de {count} usuarios: el archivo los incluye con un valor vac\u00edo.',
  'userOrgs.csv.confirmMergeClear': 'Lo entiendo: borrar los valores que el archivo deja vac\u00edos',

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
  'userOrgs.csv.noMatches':
    'Ninguna fila coincide con un usuario de esta base de datos. Compruebe que la columna de usuario contiene los nombres principales de usuario que importa este producto (por ejemplo, megan.bowen@contoso.com), no direcciones de correo ni nombres para mostrar.',
  'userOrgs.csv.previewAriaLabel': 'Vista previa del archivo',
  'userOrgs.csv.column.line': 'L\u00ednea',
  'userOrgs.csv.column.user': 'Usuario',
  'userOrgs.csv.column.organisation': 'Organizaci\u00f3n',
  'userOrgs.csv.column.matches': 'Coincide con un usuario',
  'userOrgs.csv.clearsValue': 'borra el valor',
  'userOrgs.csv.showingFirst':
    'Se muestran las primeras {count} filas. Se importa el archivo completo.',
  'userOrgs.csv.truncated.one':
    '1 nombre de organizaci\u00f3n supera los {max} caracteres y se almacenar\u00e1 abreviado. Los nombres id\u00e9nticos en sus primeros {max} caracteres se convierten en una sola organizaci\u00f3n.',
  'userOrgs.csv.truncated.other':
    '{count} nombres de organizaci\u00f3n superan los {max} caracteres y se almacenar\u00e1n abreviados. Los nombres id\u00e9nticos en sus primeros {max} caracteres se convierten en una sola organizaci\u00f3n.',
  'userOrgs.csv.problems':
    'Algunas filas no se pueden usar: {problems}. Se omiten y se contabilizan; el resto del archivo se importa igualmente.',
  'userOrgs.csv.problemLine': 'l\u00ednea {line} ({reason})',
  'userOrgs.csv.problem.missingUserColumn': 'falta la columna de usuario',
  'userOrgs.csv.problem.userEmptyOrTooLong': 'el nombre principal de usuario est\u00e1 vac\u00edo o es demasiado largo',
  'userOrgs.csv.problem.notAValidUpn': 'el valor de usuario no es un nombre principal de usuario v\u00e1lido',
  'userOrgs.csv.problem.unknownUser': 'el nombre principal de usuario no coincide con ning\u00fan usuario de esta base de datos',
  'userOrgs.csv.unusable.download.one': 'Descargar la 1 fila que no se puede importar (CSV)',
  'userOrgs.csv.unusable.download.other': 'Descargar las {count} filas que no se pueden importar (CSV)',
  'userOrgs.csv.unusable.truncated': 'La descarga enumera las primeras {shown} de {count} filas.',
  'userOrgs.csv.unusable.defaultFileName': 'organizaciones-de-usuario',
  'userOrgs.csv.unusable.column.line': 'L\u00ednea',
  'userOrgs.csv.unusable.column.user': 'Usuario',
  'userOrgs.csv.unusable.column.value': '{name}',
  'userOrgs.csv.unusable.column.reason': 'Motivo',
  'userOrgs.csv.blocking.notUtf8':
    'Este archivo no est\u00e1 guardado como UTF-8, por lo que los nombres con acentos o en alfabetos no latinos se corromper\u00edan (el primer problema est\u00e1 en la l\u00ednea {line}). En Excel, use Guardar como y elija \u00abCSV UTF-8 (delimitado por comas)\u00bb; despu\u00e9s, vuelva a elegir el archivo.',
  'userOrgs.csv.blocking.excelWorkbook':
    'Es un libro de Excel, no un archivo CSV. En Excel, use Guardar como y elija \u00abCSV UTF-8 (delimitado por comas)\u00bb; despu\u00e9s, elija ese archivo.',
  'userOrgs.csv.blocking.notText':
    'Esto no parece un archivo CSV de texto. Gu\u00e1rdelo como \u00abCSV UTF-8 (delimitado por comas)\u00bb y vuelva a elegirlo.',
  'userOrgs.csv.blocking.unterminatedQuote':
    'Una comilla de la l\u00ednea {line} no se cierra nunca, por lo que el resto del archivo no se puede leer como filas. Corrija las comillas y vuelva a elegir el archivo.',
  'userOrgs.csv.blocking.rowSpansLines':
    'Las l\u00edneas {line}-{lastLine} se han le\u00eddo como una sola fila porque una comilla de la l\u00ednea {line} no se cierra en esa l\u00ednea. Los nombres de organizaci\u00f3n no pueden contener saltos de l\u00ednea: quite la comilla sobrante, o ponga todo el valor entre comillas, y vuelva a elegir el archivo.',
  'userOrgs.csv.blocking.chooseColumns':
    'Este archivo tiene varias columnas. Elija cu\u00e1l contiene el nombre principal de usuario de cada persona y cu\u00e1l contiene el valor.',
  'userOrgs.csv.blocking.oneColumn':
    'Este archivo tiene una sola columna. Necesita dos: el nombre principal de usuario de cada persona y su valor.',
  'userOrgs.csv.blocking.tooManyRows':
    'Este archivo tiene m\u00e1s de {max} filas. Div\u00eddalo en archivos m\u00e1s peque\u00f1os e imp\u00f3rtelos de uno en uno.',
  'userOrgs.csv.blocking.noRows': 'Este archivo no tiene filas que importar.',
  'userOrgs.csv.blocking.noUsableRows': 'No se puede usar ninguna fila de este archivo.',
  'userOrgs.csv.blocking.generic': 'Este archivo no se puede importar.',
  'userOrgs.csv.apiError.importInProgress':
    'Ya hay otra importaci\u00f3n en curso para este tipo de organizaci\u00f3n. Espere a que termine y vuelva a elegir el archivo.',
  'userOrgs.csv.apiError.draftNotFound': 'Esta vista previa ha expirado o ya se import\u00f3. Vuelva a elegir el archivo.',
  'userOrgs.csv.apiError.typeChanged':
    'El tipo de organizaci\u00f3n cambi\u00f3 despu\u00e9s de la vista previa. Revise su configuraci\u00f3n y vuelva a elegir el archivo.',
  'userOrgs.csv.apiError.typeNotFound': 'Este tipo de organizaci\u00f3n ya no existe.',
  'userOrgs.csv.apiError.typeNotCsv': '{name} no es un tipo de organizaci\u00f3n basado en CSV.',
  'userOrgs.csv.apiError.typeDisabled': '{name} est\u00e1 deshabilitado. Habil\u00edtelo antes de importar un archivo.',
  'userOrgs.csv.apiError.noMatchingUsers':
    'Ninguna fila coincide con un usuario de esta base de datos. Compruebe la columna de usuario y vuelva a elegir el archivo.',
  'userOrgs.csv.apiError.clearExceedsConfirmed':
    'Esta importaci\u00f3n borrar\u00eda ahora los valores de {count} usuarios, no los {confirmed} que confirm\u00f3. Los datos han cambiado desde la vista previa; vuelva a elegir el archivo para ver las nuevas cifras.',
  'userOrgs.csv.apiError.noFile': 'Elija un archivo CSV antes de obtener la vista previa.',
  'userOrgs.csv.apiError.uploadUnreadable': 'No se pudo leer el archivo. Vuelva a elegirlo o guarde una copia nueva desde Excel.',
  'userOrgs.csv.apiError.uploadTooLarge':
    'Este archivo supera el l\u00edmite de carga de {maxMb} MB. Div\u00eddalo en archivos m\u00e1s peque\u00f1os e imp\u00f3rtelos de uno en uno.',
  'userOrgs.csv.apiError.invalidMode': 'Elija Combinar o Reemplazar antes de importar.',
  'userOrgs.csv.apiError.invalidColumns': 'Elija dos columnas distintas: una para el nombre principal de usuario y otra para el valor.',

  // Job progress
  'userOrgs.job.waiting': 'Esperando para empezar...',
  'userOrgs.job.importing': 'Importando {count} filas... iniciada a las {time}.',
  'userOrgs.job.resumed': 'Reanudada despu\u00e9s de que la aplicaci\u00f3n web se reiniciara.',
  'userOrgs.job.poll.warning':
    'No se puede contactar con el servidor para comprobar esta importaci\u00f3n. La importaci\u00f3n contin\u00faa de todos modos; se sigue intentando...',
  'userOrgs.job.poll.notFound':
    'Esta importaci\u00f3n ya no se puede encontrar. Es posible que se haya eliminado el tipo de organizaci\u00f3n.',
  'userOrgs.job.poll.sessionExpired':
    'La sesi\u00f3n ha caducado. Vuelva a cargar la p\u00e1gina para ver c\u00f3mo termin\u00f3 la importaci\u00f3n.',
  'userOrgs.job.interrupted':
    'Esta importaci\u00f3n dej\u00f3 de informar de su progreso, normalmente porque la aplicaci\u00f3n web se reinici\u00f3. No se cambi\u00f3 nada: una importaci\u00f3n se guarda en un solo paso junto con su estado correcto, as\u00ed que no puede quedar a medias. El servidor la reanudar\u00e1 autom\u00e1ticamente. Si no se ha reiniciado en unos minutos, vuelva a cargar el archivo.',
  'userOrgs.job.failed': 'La importaci\u00f3n ha {status}. {message}',
  'userOrgs.job.finished': 'Importaci\u00f3n finalizada.',
  'userOrgs.job.nothingChanged.detail': 'No ha cambiado nada. {reason}',
  'userOrgs.job.nothingChanged.unknown.one': '1 fila no coincidi\u00f3 con ning\u00fan usuario.',
  'userOrgs.job.nothingChanged.unknown.other': '{count} filas no coincidieron con ning\u00fan usuario.',
  'userOrgs.job.nothingChanged.sameValues': 'Todos ten\u00edan ya estos valores.',
  'userOrgs.job.changed': '{count} modificados',
  'userOrgs.job.cleared': '{count} borrados',
  'userOrgs.job.unknownUsers': '{count} usuario(s) desconocido(s)',
  'userOrgs.job.unusableRows': '{count} fila(s) inutilizable(s)',
  'userOrgs.job.error.failed':
    'No se pudo completar la importaci\u00f3n. No se cambi\u00f3 nada. Int\u00e9ntelo de nuevo; si sigue fallando, revise los registros del servicio.',
  'userOrgs.job.error.superseded':
    'Esta importaci\u00f3n se sustituy\u00f3 por otra posterior para el mismo tipo de organizaci\u00f3n despu\u00e9s de dejar de informar de su progreso. La importaci\u00f3n posterior es la que cuenta.',
  'userOrgs.job.error.typeChanged':
    'El tipo de organizaci\u00f3n se cambi\u00f3 despu\u00e9s de obtener la vista previa del archivo, por lo que no se import\u00f3 nada. Revise su configuraci\u00f3n y vuelva a elegir el archivo.',
  'userOrgs.job.error.clearExceedsConfirmed':
    'No se import\u00f3 nada: cuando se ejecut\u00f3, habr\u00eda borrado los valores de m\u00e1s usuarios de los que confirm\u00f3. Vuelva a elegir el archivo para ver las nuevas cifras.',
  'userOrgs.job.error.interruptedRepeatedly':
    'La aplicaci\u00f3n web se reinici\u00f3 varias veces durante esta importaci\u00f3n, as\u00ed que se detuvo. No se cambi\u00f3 nada. Vuelva a cargar el archivo.',
  'userOrgs.lastImport.line': '\u00daltima importaci\u00f3n: {date} por {who}. {outcome} {counts}',
  'userOrgs.history.title': 'Historial de importaciones',
  'userOrgs.history.loading': 'Cargando historial de importaciones...',
  'userOrgs.history.empty': 'A\u00fan no hay importaciones.',
  'userOrgs.history.column.date': 'Fecha',
  'userOrgs.history.column.who': 'Por',
  'userOrgs.history.column.mode': 'Modo',
  'userOrgs.history.column.status': 'Estado',
  'userOrgs.history.column.counts': 'Recuentos',
  'userOrgs.history.column.reason': 'Motivo',
  'userOrgs.history.mode.merge': 'Combinar',
  'userOrgs.history.mode.replace': 'Reemplazar',
  'userOrgs.history.reason.none': 'Ninguno',
  'userOrgs.history.outcome.succeeded': 'Correcta.',
  'userOrgs.history.counts':
    '{changed} modificados, {cleared} borrados, {unknown} desconocidos, {unusable} inutilizables',
  'userOrgs.history.column.changes': 'Cambios',

  // What an import changed
  'userOrgs.changes.open': 'Ver cambios',
  'userOrgs.changes.openAfterImport': 'Ver qu\u00e9 cambi\u00f3',
  'userOrgs.changes.title': 'Qu\u00e9 cambi\u00f3 esta importaci\u00f3n',
  'userOrgs.changes.close': 'Cerrar',
  'userOrgs.changes.importedBy': 'Importado el {date} por {who} ({mode}) desde {file}.',
  'userOrgs.changes.importedByNoFile': 'Importado el {date} por {who} ({mode}).',
  'userOrgs.changes.counts': '{added} a\u00f1adidos, {changed} modificados, {cleared} borrados',
  'userOrgs.changes.storage.tableStorage': 'Esta lista de cambios se guarda en Azure Table Storage.',
  'userOrgs.changes.storage.memory':
    'Esta lista de cambios solo se guarda en la memoria de este servidor web, porque no hab\u00eda ninguna cuenta de almacenamiento configurada cuando se escribi\u00f3. Se pierde cuando la aplicaci\u00f3n web se reinicia y los dem\u00e1s servidores web no pueden verla.',
  'userOrgs.changes.truncated':
    'Solo se conservaron los primeros {stored} de {count} cambios: la lista de cambios en memoria tiene un tama\u00f1o limitado.',
  'userOrgs.changes.status.pending':
    'La lista de cambios todav\u00eda se est\u00e1 escribiendo. Vuelva a intentarlo en un momento.',
  'userOrgs.changes.status.none': 'Esta importaci\u00f3n no tiene lista de cambios porque no se aplic\u00f3.',
  'userOrgs.changes.status.missingMemory':
    'Esta lista de cambios se guard\u00f3 en memoria y ya no est\u00e1 disponible: la aplicaci\u00f3n web se ha reiniciado desde entonces, o est\u00e1 en otro servidor web.',
  'userOrgs.changes.status.missingTable':
    'No se encontr\u00f3 esta lista de cambios en la cuenta de almacenamiento. Es posible que se haya eliminado.',
  'userOrgs.changes.status.unavailable':
    'No se puede acceder a la cuenta de almacenamiento en este momento, as\u00ed que no se puede mostrar la lista de cambios. Vuelva a intentarlo m\u00e1s tarde.',
  'userOrgs.changes.retry': 'Reintentar',
  'userOrgs.changes.searchPlaceholder': 'Buscar por nombre principal de usuario',
  'userOrgs.changes.tableLabel': 'Cambios realizados por esta importaci\u00f3n',
  'userOrgs.changes.column.user': 'Usuario',
  'userOrgs.changes.column.before': 'Antes',
  'userOrgs.changes.column.after': 'Despu\u00e9s',
  'userOrgs.changes.column.change': 'Cambio',
  'userOrgs.changes.kind.added': 'A\u00f1adido',
  'userOrgs.changes.kind.changed': 'Modificado',
  'userOrgs.changes.kind.cleared': 'Borrado',
  'userOrgs.changes.noValue': '(sin valor)',
  'userOrgs.changes.empty': 'Esta importaci\u00f3n no cambi\u00f3 a nadie: todos ten\u00edan ya estos valores.',
  'userOrgs.changes.noMatches':
    'No hay cambios de usuarios cuyo nombre empiece por \u00ab{search}\u00bb.',
  'userOrgs.changes.loading': 'Cargando cambios...',
  'userOrgs.changes.loadFailed': 'No se pudieron cargar los cambios.',
  'userOrgs.changes.loadMore': 'Mostrar m\u00e1s',
  'userOrgs.changes.showing': 'Mostrando {count} de {total}',
  'userOrgs.changes.download': 'Descargar todos los cambios (CSV)',
  'userOrgs.changes.downloading': 'Descargando... {count} cambios hasta ahora',
  'userOrgs.changes.downloadFailed': 'La descarga no termin\u00f3. Vuelva a intentarlo.',
  'userOrgs.changes.defaultFileName': 'cambios-organizaciones-de-usuario',

  // CSV column separators, as the preview names them
  'userOrgs.csv.delimiter.comma': 'comas',
  'userOrgs.csv.delimiter.semicolon': 'punto y coma',
  'userOrgs.csv.delimiter.tab': 'tabulaciones',
  'userOrgs.csv.delimiter.pipe': 'barras verticales',

  // Messages the API sends as a code (UserOrgMessageCodes.cs); the server's English is only a fallback
  'userOrgs.message.noType': 'No se ha indicado ning\u00fan tipo de organizaci\u00f3n.',
  'userOrgs.message.nameRequired': 'El nombre del tipo de organizaci\u00f3n es obligatorio.',
  'userOrgs.message.nameTooLong': 'El nombre de un tipo de organizaci\u00f3n puede tener como m\u00e1ximo {max} caracteres.',
  'userOrgs.message.duplicateName': 'Ya existe un tipo de organizaci\u00f3n llamado \u00ab{name}\u00bb.',
  'userOrgs.message.invalidSource':
    'Un tipo de organizaci\u00f3n toma sus valores de un atributo de Entra o de un archivo CSV.',
  'userOrgs.message.typeGone':
    'Ese tipo de organizaci\u00f3n ya no existe: es posible que se haya eliminado en otra sesi\u00f3n.',
  'userOrgs.message.importRunningChange':
    'Hay una importaci\u00f3n de {name} en curso. Espere a que termine antes de cambiar el tipo.',
  'userOrgs.message.importRunningDelete':
    'Hay una importaci\u00f3n de este tipo de organizaci\u00f3n en curso. Espere a que termine antes de eliminar el tipo.',
  'userOrgs.message.attributeRequired': 'El nombre del atributo de Entra es obligatorio.',
  'userOrgs.message.attributeTooLong': 'El nombre de un atributo puede tener como m\u00e1ximo {max} caracteres.',
  'userOrgs.message.openExtension':
    'No se pueden usar extensiones abiertas (las que se leen con $expand=extensions): Microsoft Graph no admite $expand en /users/delta, que es como este producto sigue los cambios de los usuarios. Use una extensi\u00f3n de directorio, una extensi\u00f3n de esquema o una de las ranuras extensionAttribute1-15.',
  'userOrgs.message.attributeHasSpaces':
    '\u00ab{attribute}\u00bb no es un nombre de atributo v\u00e1lido: los nombres de atributo no pueden contener espacios.',
  'userOrgs.message.badDirectoryExtension':
    '\u00ab{attribute}\u00bb parece una extensi\u00f3n de directorio, pero no tiene el formato obligatorio extension_<id. de aplicaci\u00f3n>_<nombre>, en el que el id. de aplicaci\u00f3n tiene exactamente 32 caracteres hexadecimales.',
  'userOrgs.message.tooManyDots':
    '\u00ab{attribute}\u00bb no es un nombre de atributo v\u00e1lido: tiene m\u00e1s de un separador \u00ab.\u00bb.',
  'userOrgs.message.nothingAfterDot':
    '\u00ab{attribute}\u00bb no es un nombre de atributo v\u00e1lido: no hay nada despu\u00e9s del separador \u00ab.\u00bb.',
  'userOrgs.message.notEmployeeOrgDataProperty':
    '\u00ab{property}\u00bb no es una propiedad de employeeOrgData. Microsoft Graph solo define costCenter y division.',
  'userOrgs.message.directoryExtensionSubProperty':
    '\u00ab{attribute}\u00bb no es v\u00e1lido. Una extensi\u00f3n de directorio es una \u00fanica propiedad plana, as\u00ed que no puede tener una subpropiedad con \u00ab.\u00bb.',
  'userOrgs.message.unknownContainer':
    '\u00ab{container}\u00bb no es una propiedad reconocida. Se esperaba una de las ranuras extensionAttribute1-15, employeeOrgData.costCenter, employeeOrgData.division, una extensi\u00f3n de directorio (extension_<id. de aplicaci\u00f3n>_<nombre>) o una extensi\u00f3n de esquema (<propietario>_<nombre de esquema>.<propiedad>).',
  'userOrgs.message.badSchemaProperty':
    '\u00ab{property}\u00bb no es un nombre de propiedad de extensi\u00f3n de esquema v\u00e1lido.',
  'userOrgs.message.employeeOrgDataContainer':
    'employeeOrgData es un contenedor, no un valor. Use employeeOrgData.costCenter o employeeOrgData.division.',
  'userOrgs.message.onPremisesContainer':
    'onPremisesExtensionAttributes es un contenedor, no un valor. Use una de sus ranuras, por ejemplo extensionAttribute1.',
  'userOrgs.message.unsupportedAttribute':
    '\u00ab{attribute}\u00bb no es un atributo de organizaci\u00f3n compatible. Se esperaba una de las ranuras extensionAttribute1-15, employeeId o employeeType, employeeOrgData.costCenter, employeeOrgData.division, una extensi\u00f3n de directorio (extension_<id. de aplicaci\u00f3n>_<nombre>) o una extensi\u00f3n de esquema (<propietario>_<nombre de esquema>.<propiedad>).',
  'userOrgs.message.badOnPremisesAttribute':
    '\u00ab{attribute}\u00bb no es un atributo de extensi\u00f3n local v\u00e1lido. Se esperaba de extensionAttribute1 a extensionAttribute15.',
  'userOrgs.message.noTestRequest': 'No se ha indicado ninguna solicitud de prueba.',
  'userOrgs.message.upnRequired': 'El nombre principal de usuario es obligatorio.',
  'userOrgs.message.graphAuthFailed':
    'No se pudo autenticar en Microsoft Graph. Compruebe que el secreto de cliente o el certificado del registro de aplicaci\u00f3n no ha caducado y que tiene el permiso de aplicaci\u00f3n User.Read.All con el consentimiento del administrador. Los registros del servicio tienen el detalle.',
  'userOrgs.message.userNotFound':
    'No se encontr\u00f3 ese usuario en este inquilino. Compruebe el nombre principal de usuario.',
  'userOrgs.message.propertyRejected':
    'Microsoft Graph no reconoce la propiedad \u00ab{property}\u00bb en un usuario. Compruebe el nombre del atributo: una extensi\u00f3n de directorio debe tener la forma completa extension_<id. de aplicaci\u00f3n>_<nombre>. Este atributo no se puede usar hasta que Graph lo acepte: guardarlo har\u00eda fallar todas las importaciones de usuarios.',
  'userOrgs.message.notAuthorised':
    'Este registro de aplicaci\u00f3n no tiene permiso para leer ese usuario o esa propiedad. Para leer usuarios se necesita el permiso de aplicaci\u00f3n User.Read.All, concedido con el consentimiento del administrador.',
  'userOrgs.message.throttled':
    'Microsoft Graph est\u00e1 limitando las solicitudes de este inquilino en este momento. Espere un momento y vuelva a intentarlo.',
  'userOrgs.message.graphError':
    'Microsoft Graph devolvi\u00f3 HTTP {status}. Vuelva a intentarlo en un momento; si sigue ocurriendo, los registros del servicio tienen el detalle.',
  'userOrgs.message.unreadableResponse': 'Microsoft Graph devolvi\u00f3 una respuesta que no se pudo leer.',
  'userOrgs.message.multiValued':
    '\u00ab{property}\u00bb contiene una lista de valores, no un valor, as\u00ed que no puede ser un tipo de organizaci\u00f3n: un usuario solo puede estar en una organizaci\u00f3n de cada tipo.',
  'userOrgs.message.noValue':
    'El atributo se ley\u00f3 correctamente, pero este usuario no tiene ning\u00fan valor. En una importaci\u00f3n, eso significa que se borrar\u00eda su valor de organizaci\u00f3n.',
  'userOrgs.message.wouldTruncate':
    'El valor tiene m\u00e1s de {max} caracteres y se acortar\u00eda al guardarlo.',
  'userOrgs.message.discoveryAuthFailed':
    'No se pudo autenticar en Microsoft Graph para buscar extensiones de directorio. Compruebe las credenciales y los permisos del registro de aplicaci\u00f3n; los registros del servicio tienen el detalle. Puede escribir igualmente el nombre completo de una extensi\u00f3n de directorio y probarlo.',
  'userOrgs.message.discoveryForbidden':
    'Este registro de aplicaci\u00f3n no puede enumerar las extensiones de directorio: esa llamada necesita el permiso de aplicaci\u00f3n Directory.Read.All, que es m\u00e1s de lo que requiere el resto de este producto. Puede escribir igualmente el nombre completo de una extensi\u00f3n de directorio y probarlo.',
  'userOrgs.message.discoveryGraphError':
    'Microsoft Graph no pudo enumerar las extensiones de directorio (HTTP {status}). Puede escribir igualmente el nombre completo de una extensi\u00f3n de directorio y probarlo.',
  'userOrgs.message.discoveryNoneReturned':
    'No se devolvi\u00f3 ninguna extensi\u00f3n de directorio. Seg\u00fan su documentaci\u00f3n, la llamada de detecci\u00f3n de Microsoft Graph no devuelve nada en inquilinos con m\u00e1s de 1000 entidades de servicio, as\u00ed que esto no significa necesariamente que el inquilino no tenga ninguna. Puede escribir el nombre completo de una extensi\u00f3n de directorio y probarlo.',
  'userOrgs.message.discoveryUnreachable':
    'No se pudo contactar con Microsoft Graph para enumerar las extensiones de directorio. Los registros del servicio tienen el detalle. Puede escribir igualmente el nombre completo de una extensi\u00f3n de directorio y probarlo.',

  // Who is in each organisation
  'userOrgs.browse.title': 'Qui\u00e9n pertenece a cada organizaci\u00f3n',
  'userOrgs.browse.intro':
    'Elija un tipo de organizaci\u00f3n y, despu\u00e9s, una organizaci\u00f3n para ver sus usuarios. Las organizaciones se muestran de mayor a menor; las que ya no tienen a nadie aparecen con 0.',
  'userOrgs.browse.typeLabel': 'Tipo de organizaci\u00f3n',
  'userOrgs.browse.searchOrgsPlaceholder': 'Buscar organizaciones',
  'userOrgs.browse.searchMembersPlaceholder': 'Buscar por nombre principal de usuario',
  'userOrgs.browse.orgsTableLabel': 'Organizaciones',
  'userOrgs.browse.column.organisation': 'Organizaci\u00f3n',
  'userOrgs.browse.column.users': 'Usuarios',
  'userOrgs.browse.column.user': 'Usuario',
  'userOrgs.browse.column.department': 'Departamento',
  'userOrgs.browse.column.jobTitle': 'Cargo',
  'userOrgs.browse.membersOf': 'Usuarios de {name}',
  'userOrgs.browse.pickOrg': 'Elija una organizaci\u00f3n para ver qui\u00e9n pertenece a ella.',
  'userOrgs.browse.noOrgs': 'Este tipo a\u00fan no tiene organizaciones.',
  'userOrgs.browse.noOrgMatches': 'Ninguna organizaci\u00f3n coincide con esa b\u00fasqueda.',
  'userOrgs.browse.noMembers': 'Ahora mismo no hay nadie en esta organizaci\u00f3n.',
  'userOrgs.browse.noMemberMatches': 'Nadie de esta organizaci\u00f3n coincide con esa b\u00fasqueda.',
  'userOrgs.browse.accountDisabled': 'Cuenta deshabilitada',
  'userOrgs.browse.loading': 'Cargando...',
  'userOrgs.browse.loadFailed':
    'No se ha podido cargar esta lista. Vuelva a intentarlo o actualice la p\u00e1gina: es posible que la organizaci\u00f3n se haya modificado o eliminado en otra sesi\u00f3n.',
  'userOrgs.browse.retry': 'Reintentar',
  'userOrgs.browse.showing': 'Mostrando {from}\u2013{to} de {total}',
  'userOrgs.browse.previous': 'Anterior',
  'userOrgs.browse.next': 'Siguiente',
};

export default userOrgs;
