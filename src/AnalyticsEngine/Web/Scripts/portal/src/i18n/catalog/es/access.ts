import type { access as en } from '../en/access';

const access: Record<keyof typeof en, string> = {
  'access.permission.administration.name': 'Administración',
  'access.permission.seePii.name': 'Ver PII',
  'access.permissionRequired.administration': 'Esto necesita el permiso Administración. Pida a un administrador de Entra ID que le asigne el rol de aplicación Portal.Administration.',
  'access.permissionRequired.seePii': 'Esto muestra información sobre personas individuales, lo que necesita el permiso Ver PII. Pida a un administrador de Entra ID que le asigne el rol de aplicación Portal.SeePII.',
  'access.fetchFailed': 'No se pudieron comprobar los permisos del portal ({status}).',
  'access.loading': 'Comprobando sus permisos del portal...',
  'access.checkFailed': 'No se pudieron comprobar los permisos del portal, por lo que se ocultan las características de administración y los detalles individuales.',
  'access.enforcementOff': 'Las comprobaciones de roles están desactivadas para esta implementación (EnforcePortalRoles=false), por lo que cualquier usuario que inicie sesión puede ver el área Administración y datos sobre personas individuales.',
  'access.denied.title': 'No tiene acceso a esta página',
  'access.denied.body': 'Esta página necesita el permiso {permission}. Pida a un administrador de Entra ID que le asigne el rol de aplicación {role}.',
  'access.denied.applicationId': 'Id. de aplicación: {applicationId}',
  'access.denied.applicationUnknown': 'Esta implementación no notificó el id. de aplicación.',
  'access.piiHidden.title': 'Los detalles individuales están ocultos',
  'access.piiHidden.message': 'Los detalles sobre personas individuales necesitan el permiso Ver PII. Pida a un administrador de Entra ID que le asigne el rol de aplicación {role}.',
};

export default access;
