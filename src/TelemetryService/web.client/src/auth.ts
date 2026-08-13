import {
    InteractionRequiredAuthError,
    PublicClientApplication,
    type AccountInfo,
} from '@azure/msal-browser';

interface AuthClientConfig {
    authority: string;
    clientId: string;
    scope: string;
}

export interface DashboardAuth {
    accountName: string;
    getAccessToken: () => Promise<string>;
    signOut: () => Promise<void>;
}

async function loadAuthConfig(): Promise<AuthClientConfig> {
    const response = await fetch('/api/auth/config', { cache: 'no-store' });
    if (!response.ok) {
        throw new Error(`Authentication configuration: HTTP ${response.status}`);
    }

    const config = await response.json() as AuthClientConfig;
    if (!config.authority || !config.clientId || !config.scope) {
        throw new Error('The server returned incomplete authentication configuration.');
    }

    return config;
}

export async function initializeDashboardAuth(): Promise<DashboardAuth> {
    const config = await loadAuthConfig();
    const client = new PublicClientApplication({
        auth: {
            authority: config.authority,
            clientId: config.clientId,
            redirectUri: window.location.origin,
            postLogoutRedirectUri: window.location.origin,
        },
        cache: {
            cacheLocation: 'sessionStorage',
        },
    });

    await client.initialize();
    const redirectResult = await client.handleRedirectPromise();
    const account = redirectResult?.account
        ?? client.getActiveAccount()
        ?? client.getAllAccounts()[0];

    if (!account) {
        await client.loginRedirect({ scopes: [config.scope] });
        throw new Error('Authentication redirect did not start.');
    }

    client.setActiveAccount(account);

    return {
        accountName: account.name ?? account.username,
        getAccessToken: () => acquireAccessToken(client, account, config.scope),
        signOut: () => client.logoutRedirect({ account }),
    };
}

async function acquireAccessToken(
    client: PublicClientApplication,
    account: AccountInfo,
    scope: string,
): Promise<string> {
    const request = { account, scopes: [scope] };

    try {
        const result = await client.acquireTokenSilent(request);
        return result.accessToken;
    } catch (error: unknown) {
        if (error instanceof InteractionRequiredAuthError) {
            await client.acquireTokenRedirect(request);
            throw new Error('Authentication redirect did not complete.', { cause: error });
        }
        throw error;
    }
}
