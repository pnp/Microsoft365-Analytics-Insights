export type PortalPermission = 'administration' | 'seePii';

export interface PortalAccessRoles {
  administration: string;
  seePii: string;
}

export interface PortalAccessValue {
  status: 'loading' | 'ready' | 'error';
  enforced: boolean;
  administration: boolean;
  seePii: boolean;
  roles: PortalAccessRoles;
  applicationId: string | null;
}

export const PORTAL_PERMISSION_ERROR_CODE = 'portalPermissionRequired';

export const DEFAULT_PORTAL_ACCESS_ROLES: PortalAccessRoles = {
  administration: 'Portal.Administration',
  seePii: 'Portal.SeePII',
};

export const FAIL_CLOSED_PORTAL_ACCESS: PortalAccessValue = {
  status: 'error',
  enforced: true,
  administration: false,
  seePii: false,
  roles: DEFAULT_PORTAL_ACCESS_ROLES,
  applicationId: null,
};

export const LOADING_PORTAL_ACCESS: PortalAccessValue = {
  ...FAIL_CLOSED_PORTAL_ACCESS,
  status: 'loading',
};

export const ALL_GRANTED_PORTAL_ACCESS: PortalAccessValue = {
  status: 'ready',
  enforced: true,
  administration: true,
  seePii: true,
  roles: DEFAULT_PORTAL_ACCESS_ROLES,
  applicationId: null,
};

export function permissionGranted(access: Pick<PortalAccessValue, 'administration' | 'seePii'>, permission: PortalPermission): boolean {
  return permission === 'administration' ? access.administration : access.seePii;
}

export function roleForPermission(access: Pick<PortalAccessValue, 'roles'>, permission: PortalPermission): string {
  return permission === 'administration' ? access.roles.administration : access.roles.seePii;
}
