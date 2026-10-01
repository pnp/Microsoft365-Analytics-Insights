import { describe, expect, it, vi, afterEach } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import { PortalAccessProvider, usePortalAccess } from './PortalAccessProvider';

function Probe() {
  const access = usePortalAccess();
  return <div>{access.status}:{String(access.administration)}:{String(access.seePii)}</div>;
}

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe('PortalAccessProvider', () => {
  it('fails closed when the access fetch fails', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('offline')));

    renderWithProvider(<PortalAccessProvider><Probe /></PortalAccessProvider>);

    expect(await screen.findByText('error:false:false')).toBeInTheDocument();
  });

  it('fails closed on a refused or broken reply too', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('oops', { status: 500 })));

    renderWithProvider(<PortalAccessProvider><Probe /></PortalAccessProvider>);

    expect(await screen.findByText('error:false:false')).toBeInTheDocument();
  });

  it('holds exactly what the server granted, and nothing it did not', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({
      enforced: true,
      permissions: { administration: false, seePii: true },
      roles: { administration: 'Portal.Administration', seePii: 'Portal.SeePII' },
      applicationId: '00000000-0000-0000-0000-000000000000',
    }), { status: 200, headers: { 'Content-Type': 'application/json' } })));

    renderWithProvider(<PortalAccessProvider><Probe /></PortalAccessProvider>);

    expect(await screen.findByText('ready:false:true')).toBeInTheDocument();
  });

  it('reads a reply without an enforcement flag as enforced', async () => {
    function Enforced() {
      const access = usePortalAccess();
      return <div>{access.status}:enforced={String(access.enforced)}</div>;
    }
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({
      permissions: { administration: true, seePii: true },
    }), { status: 200, headers: { 'Content-Type': 'application/json' } })));

    renderWithProvider(<PortalAccessProvider><Enforced /></PortalAccessProvider>);

    expect(await screen.findByText('ready:enforced=true')).toBeInTheDocument();
  });
});
