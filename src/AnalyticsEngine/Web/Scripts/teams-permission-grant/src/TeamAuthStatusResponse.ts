
export interface TeamAuthStatusResponse
{
    teamId: string;
    hasAuthToken: boolean;
}
export interface TeamAuthStatus
{
    teamId: string;
    authStatus: AuthTokenResponse;
}

export enum AuthTokenResponse
{
    Unknown,
    NoAuth,
    HaveAuth
}
