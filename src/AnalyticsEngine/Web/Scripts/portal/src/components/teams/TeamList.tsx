import React from 'react';
import {
  Button,
  Table,
  TableHeader,
  TableHeaderCell,
  TableRow,
  TableBody,
  Checkbox,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
} from '@fluentui/react-components';
import TeamListItem from './TeamListItem';
import { TeamAuthStatusResponse, TeamAuthStatus, AuthTokenResponse } from '../../types/TeamAuthStatus';
import ConfirmSelection from './ConfirmSelection';
import type { Team } from '@microsoft/microsoft-graph-types';
import toast from '../toast';
import { apiFetch, PortalPermissionError } from '../../api/http';
import { EN_CATALOG, useT, type TFunction } from '../../i18n';

type TeamListProps = {
  teamsList: Array<Team>;
};
type TeamListInnerProps = TeamListProps & {
  t: TFunction;
};
type TeamListState = {
  userTeamsAuthState: Array<TeamAuthStatus>;
  teamIdsToAuth: Array<string>;
  teamIdsToDeauth: Array<string>;
  isBusy: boolean;
  /** Why the Teams' authorisation status could not be read; the checkboxes stay disabled meanwhile. */
  loadError: string | null;
};

export const TEAMS_AUTH_ERROR_TEXT: Record<string, Parameters<TFunction>[0]> = Object.freeze({
  [EN_CATALOG['admin.teams.teamList.storageNotConfigured']]: 'admin.teams.teamList.storageNotConfigured',
});

export function teamAuthErrorText(t: TFunction, message: string | undefined): string {
  if (!message?.trim()) return t('admin.teams.teamList.unexpectedApiResponse');
  const key = TEAMS_AUTH_ERROR_TEXT[message];
  return key ? t(key) : message;
}

/**
 * What to tell the admin when `api/TeamsAuthAPI` answers with an error status.
 *
 * A 401 here is not an expired portal session - `apiFetch` handles that one, by the header the
 * server sets on it. It means the server holds no delegated Teams token for this admin, which no
 * amount of retrying fixes: connecting to Microsoft Teams again does.
 */
export async function teamAuthResponseErrorText(t: TFunction, response: Response): Promise<string> {
  if (response.status === 401) return t('admin.teams.teamList.connectRequired');
  const serverMessage = await response
    .json()
    .then((b) => (b && typeof b.message === 'string' ? b.message : ''))
    .catch(() => '');
  return teamAuthErrorText(t, serverMessage);
}

class TeamListInner extends React.Component<TeamListInnerProps, TeamListState> {
  constructor(props: TeamListInnerProps) {
    super(props);

    const initalAuthState: Array<TeamAuthStatus> = [];
    props.teamsList.forEach((team) => {
      initalAuthState.push({ authStatus: AuthTokenResponse.Unknown, teamId: team.id! });
    });

    this.state = {
      userTeamsAuthState: initalAuthState,
      teamIdsToAuth: [],
      teamIdsToDeauth: [],
      isBusy: true,
      loadError: null,
    };
  }

  componentDidMount() {
    this.updateTeamsAuthState();
  }

  teamToggle(checked: boolean, id: string | undefined) {
    if (!id) {
      return;
    }

    if (checked) {
      // Add team
      let alreadyAdded: boolean = false;
      this.state.teamIdsToAuth.forEach((teamId: string) => {
        if (teamId === id) {
          alreadyAdded = true;
        }
      });
      if (!alreadyAdded) {
        console.log('Will auth team id ' + id);
        this.state.teamIdsToAuth.push(id);
        this.forceUpdate();
      }

      // Remove from "remove list"
      const teamIdsToDeauthIdx = this.state.teamIdsToDeauth.indexOf(id);
      if (teamIdsToDeauthIdx > -1) {
        this.state.teamIdsToDeauth.splice(teamIdsToDeauthIdx, 1);
      }
    } else {
      // Remove team.
      // Check if it's been added to the remove list already
      let alreadyRemoved: boolean = false;
      this.state.teamIdsToDeauth.forEach((teamId: string) => {
        if (teamId === id) {
          alreadyRemoved = true;
        }
      });

      if (!alreadyRemoved) {
        // Check if this Team was already authed
        let originallyAuthed: boolean = false;
        this.state.userTeamsAuthState.forEach((team) => {
          if (team.teamId === id && team.authStatus === AuthTokenResponse.HaveAuth) {
            originallyAuthed = true;
          }
        });

        if (originallyAuthed) {
          console.log('Will de-auth previously authed team id ' + id);
          this.state.teamIdsToDeauth.push(id);
        } else {
          console.log('Will not auth team id ' + id);
        }
        this.forceUpdate();
      }

      // Remove from "add list"
      const teamIdsToAuthIdx = this.state.teamIdsToAuth.indexOf(id);
      if (teamIdsToAuthIdx > -1) {
        this.state.teamIdsToAuth.splice(teamIdsToAuthIdx, 1);
      }
    }
  }

  toggleAllTeams(checked: boolean) {
    this.state.userTeamsAuthState.forEach((teamSelection) => {
      this.teamToggle(checked, teamSelection.teamId);
    });

    this.forceUpdate();
  }

  isClickedOverrideCallback(team: Team): boolean {
    let override: boolean = false;
    this.state.teamIdsToAuth.forEach((teamAuthState) => {
      if (teamAuthState === team.id) override = true;
    });

    return override;
  }

  render() {
    // Until the current authorisation of each Team is known, a change cannot be made safely.
    const locked = this.state.isBusy || this.state.loadError !== null;
    return (
      <div>
        {this.state.loadError && (
          <MessageBar intent="error" layout="multiline" style={{ marginBlock: '12px' }}>
            <MessageBarBody>{this.state.loadError}</MessageBarBody>
            <MessageBarActions>
              <Button onClick={() => void this.updateTeamsAuthState()}>{this.props.t('admin.teams.teamList.retry')}</Button>
            </MessageBarActions>
          </MessageBar>
        )}
        <ConfirmSelection
          saveCallback={this.authSelectedTeams.bind(this)}
          authCount={this.state.teamIdsToAuth.length}
          deAuthCount={this.state.teamIdsToDeauth.length}
          isBusy={!this.state.isBusy}
        />
        <Table aria-label={this.props.t('admin.teams.teamList.ariaLabel')} size="small" style={{ marginBlock: '12px' }}>
          <TableHeader>
            <TableRow>
              <TableHeaderCell style={{ width: 420 }}>
                <Checkbox
                  disabled={locked}
                  onChange={(_e: any, data: any) => this.toggleAllTeams(data.checked === true)}
                  label={this.props.t('admin.teams.teamList.columnTeamName')}
                />
              </TableHeaderCell>
              <TableHeaderCell style={{ width: 400 }}>{this.props.t('admin.teams.teamList.columnGraphId')}</TableHeaderCell>
              <TableHeaderCell style={{ width: 140 }}>{this.props.t('admin.teams.teamList.columnAuthorised')}</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {
              // Loop through user Teams data from Graph
              this.props.teamsList.map((team: Team, i: number) => {
                // Get auth status from our own API response
                let authState: TeamAuthStatus | null = null;
                this.state.userTeamsAuthState.forEach((teamAuthState) => {
                  if (teamAuthState.teamId === team.id) authState = teamAuthState;
                });

                return (
                  <TeamListItem
                    team={team}
                    key={i}
                    authState={authState!}
                    isBusy={locked}
                    isClickedOverrideCallback={this.isClickedOverrideCallback.bind(this)}
                    teamToggleCallback={(checked: boolean, id: string | undefined) => this.teamToggle(checked, id)}
                  />
                );
              })
            }
          </TableBody>
        </Table>
        <ConfirmSelection
          saveCallback={this.authSelectedTeams.bind(this)}
          authCount={this.state.teamIdsToAuth.length}
          deAuthCount={this.state.teamIdsToDeauth.length}
          isBusy={!this.state.isBusy}
        />
      </div>
    );
  }

  // 2nd stage load: auth-state for each team user has access to
  async updateTeamsAuthState() {
    const teamIds: Array<string> = [];

    this.setState({ isBusy: true, loadError: null });

    // Get list of team Ids
    this.props.teamsList.forEach((team) => {
      if (team.id) {
        teamIds.push(team.id);
      }
    });

    // Is the ASP.Net API defined? If we're debugging from the react app only, it won't be.
    if (window.o365AnalyticsAuthAPI) {
      let response: Response;
      try {
        response = await apiFetch(window.o365AnalyticsAuthAPI, {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            Accept: 'application/json',
          },
          body: JSON.stringify(teamIds),
        });
      } catch (err) {
        console.log(err);
        // A refusal carries its own translated reason; anything else has no message worth showing.
        const loadError = err instanceof PortalPermissionError ? err.message : teamAuthErrorText(this.props.t, undefined);
        this.setState({ isBusy: false, loadError });
        return;
      }

      if (!response.ok) {
        const loadError = await teamAuthResponseErrorText(this.props.t, response);
        this.setState({ isBusy: false, loadError });
        return;
      }

      const responseJson: Array<TeamAuthStatusResponse> = await response.json();

      // For existing authed teams, add for re-auth
      responseJson.forEach((r) => {
        if (r.hasAuthToken) {
          this.state.teamIdsToAuth.push(r.teamId);
          console.info(`Adding team ${r.teamId} for re-auth`);
        }
      });

      // Build UI state
      const loadedAuthState: Array<TeamAuthStatus> = [];
      responseJson.forEach((teamAuthState) => {
        if (teamAuthState.hasAuthToken) {
          loadedAuthState.push({ authStatus: AuthTokenResponse.HaveAuth, teamId: teamAuthState.teamId });
        } else loadedAuthState.push({ authStatus: AuthTokenResponse.NoAuth, teamId: teamAuthState.teamId });
      });

      // Set state for render
      this.setState({ userTeamsAuthState: loadedAuthState, isBusy: false });
    }
  }

  // Save config via REST call
  async authSelectedTeams() {
    this.setState({ isBusy: true });

    // No Graph token is sent: the server authorises each Team with the refresh token it holds in
    // the auth cookie (see TeamsAuthAPIController.Put), so a token in the body would be ignored.
    const body = {
      teamIdsToAuth: this.state.teamIdsToAuth,
      teamIdsToDeauth: this.state.teamIdsToDeauth,
    };

    return apiFetch(window.o365AnalyticsAuthAPI, {
      method: 'PUT',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(body),
    })
      .then(async (response) => {
        if (!response.ok) {
          // Surface the server's reason (e.g. "Azure Storage is not configured") when present.
          console.log(response);
          toast.error(await teamAuthResponseErrorText(this.props.t, response));
        } else {
          toast(this.props.t('admin.teams.teamList.saveSuccess'));
          window.location.reload();
        }
        this.setState({ isBusy: false });
      })
      .catch((err) => {
        this.showApiError(err);
      });
  }

  showApiError(errOrResponse: unknown, message?: string) {
    console.log(errOrResponse);
    toast.error(teamAuthErrorText(this.props.t, message));
    this.setState({ isBusy: false });
  }
}

export default function TeamList(props: TeamListProps) {
  const t = useT();
  return <TeamListInner {...props} t={t} />;
}
