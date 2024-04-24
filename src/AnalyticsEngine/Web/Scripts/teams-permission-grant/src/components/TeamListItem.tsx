import React from 'react';
import { } from '../WindowInterface'
import { TeamAuthStatus, AuthTokenResponse } from '../TeamAuthStatusResponse'
import { Team } from '@microsoft/microsoft-graph-types';

type TeamSelectionProps = {
    authState: TeamAuthStatus;
    teamToggleCallback: Function;
    isClickedOverrideCallback: Function;
    team: Team;
    isBusy: boolean;
}


declare global {
    interface Window {
        o365AnalyticsClientId: string;
        o365AnalyticsAuthority: string;
        o365AnalyticsRedirectUri: string;
        o365AnalyticsTokenAPI: string;
        o365AnalyticsAuthAPI: string;
    }
}
export default class TeamListItem extends React.Component<TeamSelectionProps, { selectedForAuth: boolean }>  {

    toggleTeam(e: React.ChangeEvent<HTMLInputElement>) {
        this.setState({ selectedForAuth: e.target.checked });
        this.props.teamToggleCallback(e.target.checked, this.props.team.id);
    }
    render() {
        var o: boolean = this.props.isClickedOverrideCallback(this.props.team);
        return (
            <tr>
                <td>
                    <div className="form-check">
                        <label className="form-check-label">
                            <input type="checkbox" checked={o} onChange={(e) => this.toggleTeam(e)} className="form-check-input" disabled={this.props.isBusy} />
                            {this.props.team.displayName}
                        </label>
                    </div>
                </td>
                <td>
                    <pre>{this.props.team.id}</pre>
                </td>
                <td>
                    {this.props.authState && this.props.authState.authStatus !== AuthTokenResponse.Unknown ?
                        (
                            <div>
                                {this.props.authState.authStatus === AuthTokenResponse.HaveAuth ?
                                    (
                                        <div>
                                            {/* Yes */}
                                            <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" className="bi bi-check-circle" viewBox="0 0 16 16">
                                                <path d="M8 15A7 7 0 1 1 8 1a7 7 0 0 1 0 14zm0 1A8 8 0 1 0 8 0a8 8 0 0 0 0 16z" />
                                                <path d="M10.97 4.97a.235.235 0 0 0-.02.022L7.477 9.417 5.384 7.323a.75.75 0 0 0-1.06 1.06L6.97 11.03a.75.75 0 0 0 1.079-.02l3.992-4.99a.75.75 0 0 0-1.071-1.05z" />
                                            </svg>
                                        </div>

                                    )
                                    : (
                                        <div>
                                            {/* No */}
                                            <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" className="bi bi-circle" viewBox="0 0 16 16">
                                                <path d="M8 15A7 7 0 1 1 8 1a7 7 0 0 1 0 14zm0 1A8 8 0 1 0 8 0a8 8 0 0 0 0 16z" />
                                            </svg>
                                        </div>

                                    )
                                }
                            </div>
                        )
                        : (<p>--</p>)
                    }
                </td>
            </tr>
        );
    }

    formatDate(dateString: string) {
        var date = new Date(dateString);
        return date.toLocaleString();
    }

}
