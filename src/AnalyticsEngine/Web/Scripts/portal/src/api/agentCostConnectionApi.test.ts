import { afterEach, describe, expect, it, vi } from 'vitest';
import { beginAgentCostConnection, disconnectAgentCostConnection, fetchAgentCostConnection } from './agentCostConnectionApi';

afterEach(() => vi.unstubAllGlobals());

describe('billing connection API transport', () => {
  it('uses same-origin POST for changes and never sends a credential', async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify({ state: 'disconnected' }), { status: 200 }));
    vi.stubGlobal('fetch', fetch);
    await disconnectAgentCostConnection();
    const [url, options] = fetch.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/api/AgentCostConnection/disconnect');
    expect(options.method).toBe('POST');
    expect(new Headers(options.headers).get('X-Requested-With')).toBe('XMLHttpRequest');
    expect(options.body).toBeUndefined();
    expect(new Headers(options.headers).has('Authorization')).toBe(false);
  });

  it('maps stable storage errors to translated text', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ code: 'storageUnavailable' }), { status: 503 })));
    await expect(fetchAgentCostConnection()).rejects.toThrow(/Azure Table state/);
  });

  it('rejects a foreign redirect even if a malformed response provides one', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ url: 'https://untrusted.example/Account/ConnectAgentCosts' }), { status: 200 })));
    await expect(beginAgentCostConnection()).rejects.toThrow(/billing connection could not/);
  });

  it('only accepts the fixed local connection endpoint', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ url: '/Account/ConnectAgentCosts?intent=synthetic' }), { status: 200 })));
    expect(await beginAgentCostConnection()).toBe(`${window.location.origin}/Account/ConnectAgentCosts?intent=synthetic`);
  });
});
