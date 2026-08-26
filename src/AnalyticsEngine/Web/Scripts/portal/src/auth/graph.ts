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

  return response.json();
};

export const GRAPH_ENDPOINTS = {
  ME: 'https://graph.microsoft.com/v1.0/me',
  JOINED_TEAMS: 'https://graph.microsoft.com/v1.0/me/joinedTeams?$select=id,displayName',
};
