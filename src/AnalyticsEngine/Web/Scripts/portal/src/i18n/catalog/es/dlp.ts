import type { dlp as en } from '../en/dlp';

/**
 * Spanish (es-ES) text for the DLP impact page.
 *
 * Typed against the English module, so a key added there without a translation here fails the
 * build rather than reaching a customer as English text inside a Spanish page.
 */
const dlp: Record<keyof typeof en, string> = {
  // Encabezado y controles de página
  'dlp.title': 'Impacto de DLP en Copilot',
  'dlp.intro': 'Dónde las directivas de Prevención de pérdida de datos de Microsoft Purview impidieron que Microsoft 365 Copilot usara contenido: qué agentes y personas se ven más afectados, y qué directivas son responsables.',
  'dlp.period.ariaLabel': 'Periodo de informe',
  'dlp.period.last7Days': 'Últimos 7 días',
  'dlp.period.last28Days': 'Últimos 28 días',
  'dlp.period.last90Days': 'Últimos 90 días',
  'dlp.period.last180Days': 'Últimos 180 días',
  'dlp.loading': 'Cargando datos de DLP...',
  'dlp.error.loadData': 'No se han podido cargar los datos de DLP.',


  // Server-authored availability reasons
  'dlp.availability.reason.copilotImportOff': "La importación de auditoría de Microsoft 365 Copilot está desactivada, por lo que no hay datos de DLP por agente. Habilite 'Interacciones de Copilot' en el instalador. Esto NO necesita el permiso de DLP: los bloqueos de DLP de Copilot se incluyen en los propios registros de interacción de Copilot.",
  'dlp.availability.reason.tenantImportOff': "La importación de Data Loss Prevention (DLP.All) está desactivada, por lo que no se muestra la actividad de directivas de todo el inquilino para Exchange, SharePoint/OneDrive y Endpoint. Habilite 'Eventos de directiva DLP' en el instalador y conceda a la aplicación en tiempo de ejecución el permiso de aplicación 'ActivityFeed.ReadDlp', que es independiente de 'ActivityFeed.Read' y necesita su propio consentimiento de administrador.",

  // Columnas de tabla comunes y estados vacíos
  'dlp.table.empty': 'Nada en este periodo.',
  'dlp.column.agent': 'Agente',
  'dlp.column.user': 'Usuario',
  'dlp.column.policy': 'Directiva',
  'dlp.column.sensitivityLabel': 'Etiqueta de confidencialidad',
  'dlp.column.blocked': 'Bloqueado',
  'dlp.column.auditedOnly': 'Solo auditado',
  'dlp.column.users': 'Usuarios',
  'dlp.policyDrilldown.title': 'Directivas que afectan a {name}',
  'dlp.policyDrilldown.thisAgent': 'este agente',

  // KPI de resumen
  'dlp.kpi.blocked.label': 'Bloqueado',
  'dlp.kpi.blocked.hint': 'A Copilot se le denegó el contenido',
  'dlp.kpi.auditedOnly.label': 'Solo auditado',
  'dlp.kpi.auditedOnly.hint': 'La directiva coincidió, no se retuvo nada',
  'dlp.kpi.peopleAffected.label': 'Personas afectadas',
  'dlp.kpi.peopleAffected.hint': 'Usuarios distintos bloqueados',
  'dlp.kpi.agentsAffected.label': 'Agentes afectados',
  'dlp.kpi.agentsAffected.hint': 'Agentes distintos bloqueados',
  'dlp.kpi.policiesInvolved.label': 'Directivas implicadas',
  'dlp.kpi.policiesInvolved.hint': 'Directivas DLP distintas',
  'dlp.noCopilotActivity': 'Ninguna directiva DLP afectó a Copilot en este periodo. Si esperaba actividad, recuerde que un cambio de directiva puede tardar hasta cuatro horas en llegar a Copilot y que la directiva debe tener como destino la ubicación "Microsoft 365 Copilot and Copilot Chat".',

  // Tablas de impacto de Copilot
  'dlp.affected.title': 'Quién y qué se ve afectado',
  'dlp.affected.agents.title': 'Agentes',
  'dlp.affected.agents.description': 'Agentes de Copilot cuyo acceso al contenido se vio afectado por una directiva DLP. Esta es la única vista que puede atribuir un bloqueo de DLP a un agente específico. Seleccione un agente para ver qué directivas le afectaron.',
  'dlp.affected.people.title': 'Personas',
  'dlp.affected.people.description': 'Usuarios cuyas solicitudes de Copilot se vieron afectadas con más frecuencia. Seleccione una persona para ver qué directivas le afectaron.',
  'dlp.affected.policies.title': 'Directivas',
  'dlp.affected.policies.description': "Las directivas DLP responsables. Una directiva con bloqueos en la columna 'Solo auditado' coincide sin retener nada, normalmente porque sus reglas están en modo de simulación.",
  'dlp.affected.sensitivityLabels.title': 'Etiquetas de confidencialidad',
  'dlp.affected.sensitivityLabels.description': "Etiquetas del contenido que se impidió usar a Copilot. La forma más común de directiva DLP para Copilot es 'impedir que Copilot procese contenido con la etiqueta X', por lo que esta suele ser la explicación.",

  // Señales de gobernanza de Copilot (#648)
  'dlp.governance.title': 'Señales de gobernanza de Copilot',
  'dlp.governance.description': 'Señales de seguridad de las indicaciones y de fundamentación que Microsoft registra en cada interacción de Copilot de este periodo. Una tasa solo cuenta las interacciones en las que Microsoft notificó la señal: que falte una marca significa que no se notificó, no que la interacción fuera segura.',
  'dlp.governance.loading': 'Cargando las señales de gobernanza...',
  'dlp.governance.error': 'No se han podido cargar las señales de gobernanza.',
  'dlp.governance.importOff': 'La importación de auditoría de Copilot está desactivada, así que no hay señales de gobernanza que mostrar.',
  'dlp.governance.noInteractions': 'No hay interacciones de Copilot en este periodo.',
  'dlp.governance.rate.value': '{rate} por cada 10.000',
  'dlp.governance.rate.notReported': 'No notificado',
  'dlp.governance.rate.fraction': '{flagged} de las {reported} interacciones en las que Microsoft notificó la señal',
  'dlp.governance.rate.coverage': 'Notificada en {reported} de {interactions} interacciones de este periodo ({share}).',
  'dlp.governance.rate.noneReported': 'Microsoft no notificó esta señal en ninguna de las {interactions} interacciones de este periodo, así que no hay tasa.',
  'dlp.governance.jailbreak.title': 'Intentos de jailbreak',
  'dlp.governance.jailbreak.hint': 'Interacciones con una indicación que Microsoft marcó como un intento de eludir las protecciones de Copilot.',
  'dlp.governance.xpia.title': 'Inyección de indicaciones cruzadas (XPIA)',
  'dlp.governance.xpia.hint': 'Interacciones en las que Microsoft detectó instrucciones insertadas en contenido que usó Copilot.',
  'dlp.governance.labels.title': 'Contenido etiquetado usado',
  'dlp.governance.labels.fraction': '{labelled} de los {resources} recursos que usó Copilot tenían una etiqueta de confidencialidad',
  'dlp.governance.labels.coverage': 'Copilot usó contenido en {withResources} de {interactions} interacciones. Un recurso cuenta una vez por cada interacción que lo usó.',
  'dlp.governance.labels.none': 'No se usó contenido',
  'dlp.governance.agents.title': 'Agentes con bloqueos de DLP',
  'dlp.governance.agents.hint': 'Agentes distintos que una directiva DLP bloqueó en este periodo. Los más bloqueados:',
  'dlp.governance.agents.blocked': 'Bloqueos: {count}',
  'dlp.governance.agents.none': 'No se bloqueó ningún agente en este periodo.',
  'dlp.governance.agents.pointer': 'Si un agente sigue mereciendo la pena se muestra en {link}, donde cada agente tiene un veredicto como Retirar o Revisar.',
  'dlp.governance.agents.pointerLink': 'Adopción de Copilot > Agentes',
  'dlp.governance.models.title': 'Modelos de IA',
  'dlp.governance.models.description': 'Los modelos que Microsoft indicó, cada uno como proporción de las {interactions} interacciones de este periodo. Se indicó un modelo en {withModel} de ellas: Microsoft no indica el modelo en la mayoría de las interacciones de Microsoft 365 Copilot, así que las proporciones no suman el 100 %. DEEP_LEO es el modelo que Microsoft notifica cuando Copilot usó razonamiento profundo.',
  'dlp.governance.models.empty': 'Microsoft no indicó ningún modelo en este periodo.',
  'dlp.governance.plugins.title': 'Complementos del sistema',
  'dlp.governance.plugins.description': 'Los complementos que invocó Copilot, cada uno como proporción de las {interactions} interacciones de este periodo. Copilot invocó un complemento en {withPlugin} de ellas. BingWebSearch significa que Copilot buscó en la web pública.',
  'dlp.governance.plugins.empty': 'Copilot no invocó ningún complemento en este periodo.',
  'dlp.governance.column.model': 'Modelo',
  'dlp.governance.column.plugin': 'Complemento',
  'dlp.governance.column.interactions': 'Interacciones',
  'dlp.governance.column.share': 'Proporción de interacciones',

  // Actividad DLP de todo el inquilino
  'dlp.tenant.title': 'Actividad DLP de todo el inquilino',
  'dlp.tenant.description': 'Actividad de directivas DLP en Exchange, SharePoint/OneDrive y dispositivos de punto de conexión, desde la fuente de auditoría DLP independiente. Estos registros identifican a la persona, pero nunca al agente de Copilot, por lo que se notifican por separado y no se combinan con las cifras de Copilot anteriores.',
  'dlp.tenant.importOff': 'La importación de DLP está desactivada, así que no hay nada que mostrar aquí.',
  'dlp.tenant.blocked.hint': 'En todas las cargas de trabajo',
  'dlp.tenant.auditedOnly.hint': 'Coincidencia sin cumplimiento',
  'dlp.tenant.policies.title': 'Directivas (todo el inquilino)',
  'dlp.tenant.policies.description': 'Directivas que se activan en todo el inquilino, desde la fuente de auditoría DLP.',
  'dlp.tenant.titleFiltered': 'Actividad DLP en todas las cargas de trabajo',
  'dlp.tenant.policies.titleFiltered': 'Directivas (todas las cargas de trabajo)',
  'dlp.tenant.policies.descriptionFiltered': 'Directivas que se activan para las personas que incluye el filtro del administrador, desde la fuente de auditoría DLP.',
};

export default dlp;
