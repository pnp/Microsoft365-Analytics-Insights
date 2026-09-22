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

  // Actividad DLP de todo el inquilino
  'dlp.tenant.title': 'Actividad DLP de todo el inquilino',
  'dlp.tenant.description': 'Actividad de directivas DLP en Exchange, SharePoint/OneDrive y dispositivos de punto de conexión, desde la fuente de auditoría DLP independiente. Estos registros identifican a la persona, pero nunca al agente de Copilot, por lo que se notifican por separado y no se combinan con las cifras de Copilot anteriores.',
  'dlp.tenant.importOff': 'La importación de DLP está desactivada, así que no hay nada que mostrar aquí.',
  'dlp.tenant.blocked.hint': 'En todas las cargas de trabajo',
  'dlp.tenant.auditedOnly.hint': 'Coincidencia sin cumplimiento',
  'dlp.tenant.policies.title': 'Directivas (todo el inquilino)',
  'dlp.tenant.policies.description': 'Directivas que se activan en todo el inquilino, desde la fuente de auditoría DLP.',
};

export default dlp;
