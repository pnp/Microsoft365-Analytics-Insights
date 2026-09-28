import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { fetchPortalAccess } from '../api/portalAccessApi';
import {
  DEFAULT_PORTAL_ACCESS_ROLES,
  FAIL_CLOSED_PORTAL_ACCESS,
  LOADING_PORTAL_ACCESS,
  type PortalAccessValue,
} from './types';

export type PortalAccessProviderValue = Partial<PortalAccessValue>;

const PortalAccessContext = createContext<PortalAccessValue>(FAIL_CLOSED_PORTAL_ACCESS);

function normalise(value: PortalAccessProviderValue): PortalAccessValue {
  return {
    status: value.status ?? 'ready',
    enforced: value.enforced ?? true,
    administration: value.administration ?? false,
    seePii: value.seePii ?? false,
    roles: { ...DEFAULT_PORTAL_ACCESS_ROLES, ...(value.roles ?? {}) },
    applicationId: value.applicationId ?? null,
  };
}

export function PortalAccessProvider({ children, value }: { children: ReactNode; value?: PortalAccessProviderValue }) {
  const explicit = useMemo(() => value ? normalise(value) : null, [value]);
  const [access, setAccess] = useState<PortalAccessValue>(explicit ?? LOADING_PORTAL_ACCESS);

  useEffect(() => {
    if (explicit) {
      setAccess(explicit);
      return;
    }

    let cancelled = false;
    setAccess(LOADING_PORTAL_ACCESS);
    fetchPortalAccess()
      .then((grant) => {
        if (!cancelled) setAccess({ ...normalise(grant), status: 'ready' });
      })
      .catch(() => {
        if (!cancelled) setAccess(FAIL_CLOSED_PORTAL_ACCESS);
      });

    return () => {
      cancelled = true;
    };
  }, [explicit]);

  return <PortalAccessContext.Provider value={access}>{children}</PortalAccessContext.Provider>;
}

export function usePortalAccess(): PortalAccessValue {
  return useContext(PortalAccessContext);
}
