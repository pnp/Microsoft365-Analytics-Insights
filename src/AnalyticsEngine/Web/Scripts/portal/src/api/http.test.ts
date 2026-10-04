import { afterEach, describe, expect, it, vi } from 'vitest';
import { PortalPermissionError, ReportScopeError, apiFetch } from './http';
import { setActiveLanguage } from '../i18n/runtime';

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  setActiveLanguage('en');
});

describe('apiFetch portal permission errors', () => {
  it('throws a translated PortalPermissionError for the portal permission 403 body', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({
      code: 'portalPermissionRequired',
      permission: 'seePii',
      role: 'Portal.SeePII',
      message: 'server fallback',
    }), { status: 403, headers: { 'Content-Type': 'application/json' } })));

    await expect(apiFetch('/api/example')).rejects.toMatchObject({
      name: 'PortalPermissionError',
      permission: 'seePii',
      role: 'Portal.SeePII',
      message: 'This shows information about individual people, which needs the See PII permission. Ask an Entra ID administrator to assign you the Portal.SeePII app role.',
    });
  });

  it('leaves unrelated 403 responses readable by callers', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ code: 'other' }), {
      status: 403,
      headers: { 'Content-Type': 'application/json' },
    })));

    const response = await apiFetch('/api/example');
    expect(response.status).toBe(403);
    expect(await response.json()).toEqual({ code: 'other' });
    expect(new PortalPermissionError('administration', 'Portal.Administration').permission).toBe('administration');
  });
});

describe('apiFetch global filter refusals', () => {
  it('throws a translated ReportScopeError when a report is refused because the global filter cannot be applied', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({
      code: 'filterDirectoryUnavailable',
      message: 'Server English.',
    }), { status: 503, headers: { 'Content-Type': 'application/json' } })));

    await expect(apiFetch('/api/WebActivity/overview')).rejects.toMatchObject({
      name: 'ReportScopeError',
      code: 'filterDirectoryUnavailable',
      message: "The user directory that report filters are applied to couldn't be read, so this filtered report isn't available right now. Try again shortly.",
    });

    setActiveLanguage('es');
    await expect(apiFetch('/api/WebActivity/overview')).rejects.toBeInstanceOf(ReportScopeError);
  });

  it('leaves every other 503 to the report that sent it', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ code: 'licenceReportingBusy' }), {
      status: 503,
      headers: { 'Content-Type': 'application/json' },
    })));

    const response = await apiFetch('/api/LicenceActivity/overview');
    expect(response.status).toBe(503);
    expect(await response.json()).toEqual({ code: 'licenceReportingBusy' });
  });
});
