import { apiFetch } from './http';
import { DEFAULT_PORTAL_ACCESS_ROLES, type PortalAccessValue } from '../access/types';
import { translateActive } from '../i18n/runtime';

type PortalAccessResponse = {
  enforced?: unknown;
  permissions?: { administration?: unknown; seePii?: unknown } | null;
  roles?: { administration?: unknown; seePii?: unknown } | null;
  applicationId?: unknown;
};

function bool(value: unknown): boolean {
  return value === true;
}

function text(value: unknown, fallback: string): string {
  return typeof value === 'string' && value.length > 0 ? value : fallback;
}

export async function fetchPortalAccess(): Promise<Omit<PortalAccessValue, 'status'>> {
  const response = await apiFetch(`${window.location.origin}/api/PortalAccess`, {
    method: 'GET',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw new Error(translateActive('access.fetchFailed', { status: response.status }));
  }

  const body = (await response.json()) as PortalAccessResponse;
  return {
    // Only an explicit false means role checks are off; anything else is read as on, so the portal
    // never wrongly tells an administrator that every signed-in user can see everything.
    enforced: body.enforced !== false,
    administration: bool(body.permissions?.administration),
    seePii: bool(body.permissions?.seePii),
    roles: {
      administration: text(body.roles?.administration, DEFAULT_PORTAL_ACCESS_ROLES.administration),
      seePii: text(body.roles?.seePii, DEFAULT_PORTAL_ACCESS_ROLES.seePii),
    },
    applicationId: typeof body.applicationId === 'string' && body.applicationId.length > 0 ? body.applicationId : null,
  };
}
