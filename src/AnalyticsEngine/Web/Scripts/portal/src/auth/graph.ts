/**
 * Small Microsoft Graph helpers used by the Teams permissions page.
 *
 * The browser calls Graph directly with the access token the site mints for the signed-in admin
 * (`POST api/SiteTokenAPI`). There is no client-side sign-in here: the user is already
 * authenticated to the site by the server's OIDC redirect.
 */
export const fetchMsGraph = async (url: string, accessToken: string) => {
  const response = await fetch(url, {
    headers: {
      Authorization: `Bearer ${accessToken}`,
    },
  });

  // A rejected token or a missing permission comes back as a JSON error body. Reading that as data made the
  // Teams permissions page say "No Teams found" instead of reporting that the call failed.
  if (!response.ok) {
    throw new GraphRequestError(response.status);
  }

  return response.json();
};

/** A Graph call answered with an HTTP error. Callers show their own translated message; this carries the status. */
export class GraphRequestError extends Error {
  readonly status: number;

  constructor(status: number) {
    super(String(status));
    this.status = status;
  }
}

export const GRAPH_ENDPOINTS = {
  ME: 'https://graph.microsoft.com/v1.0/me',
  JOINED_TEAMS: 'https://graph.microsoft.com/v1.0/me/joinedTeams?$select=id,displayName',
};
