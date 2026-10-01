export const access = {
  'access.permission.administration.name': 'Administration',
  'access.permission.seePii.name': 'See PII',
  'access.permissionRequired.administration': 'This needs the Administration permission. Ask an Entra ID administrator to assign you the Portal.Administration app role.',
  'access.permissionRequired.seePii': 'This shows information about individual people, which needs the See PII permission. Ask an Entra ID administrator to assign you the Portal.SeePII app role.',
  'access.fetchFailed': "Couldn't check portal permissions ({status}).",
  'access.loading': 'Checking your portal permissions...',
  'access.checkFailed': 'Portal permissions could not be checked, so administration features and individual details are hidden.',
  'access.enforcementOff': 'Role checks are switched off for this deployment (EnforcePortalRoles=false), so every signed-in user can see the Administration area and data about individual people.',
  'access.denied.title': 'You do not have access to this page',
  'access.denied.body': 'This page needs the {permission} permission. Ask an Entra ID administrator to assign you the {role} app role.',
  'access.denied.applicationId': 'Application ID: {applicationId}',
  'access.denied.applicationUnknown': 'The application ID was not reported by this deployment.',
  'access.piiHidden.title': 'Individual details are hidden',
  'access.piiHidden.message': 'Details about individual people need the See PII permission. Ask an Entra ID administrator to assign you the {role} app role.',
} as const;

export default access;
