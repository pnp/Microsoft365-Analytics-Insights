import React from 'react';
import TeamListItem from './TeamListItem';
import {TeamAuthStatusResponse, TeamAuthStatus, AuthTokenResponse} from '../TeamAuthStatusResponse'
import ConfirmSelection from './ConfirmSelection';
import { Team } from "@microsoft/microsoft-graph-types";
import toast, { Toaster } from 'react-hot-toast';

type TeamListProps = {
    teamsList: Array<Team>
}
type TeamListState = {
    userTeamsAuthState: Array<TeamAuthStatus>;
    teamIdsToAuth: Array<string>;
    teamIdsToDeauth: Array<string>;
    isBusy: boolean;
}
export default class TeamList extends React.Component<TeamListProps, TeamListState> {

    constructor(props: TeamListProps) {
        super(props);

        const initalAuthState : Array<TeamAuthStatus> = [];
        props.teamsList.forEach(team => 
        {
            initalAuthState.push({authStatus: AuthTokenResponse.Unknown, teamId: team.id!});
        });

        this.state =
        {
            userTeamsAuthState: initalAuthState,
            teamIdsToAuth: [],
            teamIdsToDeauth: [],
            isBusy: true
        };
    }

    
    componentDidMount() {
        this.updateTeamsAuthState();
    }

    teamToggle(checked: boolean, id: string) {
        if (checked) {

            // Add team
            let alreadyAdded: boolean = false;
            this.state.teamIdsToAuth.forEach((teamId: string) => {
                if (teamId === id) {
                    alreadyAdded = true;
                }
            });
            if (!alreadyAdded) {
                console.log('Will auth team id ' + id)
                this.state.teamIdsToAuth.push(id);
                this.forceUpdate();
            }

            // Remove from "remove list"
            let teamIdsToDeauthIdx = this.state.teamIdsToDeauth.indexOf(id);
            if (teamIdsToDeauthIdx > -1) {
                this.state.teamIdsToDeauth.splice(teamIdsToDeauthIdx, 1);
            }
        }
        else {
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
                let originallyAuthed : boolean = false;
                this.state.userTeamsAuthState.forEach(team => 
                {
                    if (team.teamId === id && team.authStatus === AuthTokenResponse.HaveAuth) {
                        originallyAuthed = true;
                    }
                });

                if (originallyAuthed) {
                    console.log('Will de-auth previously authed team id ' + id)
                    this.state.teamIdsToDeauth.push(id);
                }
                else
                {
                    console.log('Will not auth team id ' + id)
                }
                this.forceUpdate();
            }

            // Remove from "add list"
            let teamIdsToAuthIdx = this.state.teamIdsToAuth.indexOf(id);
            if (teamIdsToAuthIdx > -1) {
                this.state.teamIdsToAuth.splice(teamIdsToAuthIdx, 1);
            }
        }
    }

    toggleAllTeams(e: React.ChangeEvent<HTMLInputElement>)
    {
        this.state.userTeamsAuthState.forEach(teamSelection => 
        {
            this.teamToggle(e.target.checked, teamSelection.teamId);
        });
        
        console.log("Toggle all");
        this.forceUpdate();
    }

    isClickedOverrideCallback(team : Team) : boolean
    {
        
        var override: boolean = false;
        this.state.teamIdsToAuth.forEach(teamAuthState => {
            if (teamAuthState === team.id)
                override = true;
        });

        return override;
    }

    render() {
        return (

            <div>
                <Toaster />
                <ConfirmSelection saveCallback={this.authSelectedTeams.bind(this)} 
                    authCount={this.state.teamIdsToAuth.length} 
                    deAuthCount={this.state.teamIdsToDeauth.length} 
                    isBusy={!this.state.isBusy} />
                <table>
                    <thead>
                        <tr>
                            <th style={{ width: 400 }}>
                                <div className="form-check">
                                    <label className="form-check-label">
                                        <input type="checkbox" className="form-check-input" onChange={(e) => this.toggleAllTeams(e)} disabled={this.state.isBusy} />Team Name
                                    </label>
                                </div>
                            </th>
                            <th style={{width: 400}}>Graph ID</th>
                            <th style={{ width: 200 }}><p>Authorised?</p></th>
                        </tr>
                    </thead>
                    <tbody>
                        {
                            // Loop through user Teams data from Graph
                            this.props.teamsList.map((team: Team, i: number) => {

                                // Get auth status from our own API response
                                var authState: TeamAuthStatus | null = null;
                                this.state.userTeamsAuthState.forEach(teamAuthState => {
                                    if (teamAuthState.teamId === team.id)
                                        authState = teamAuthState;
                                });

                                return <TeamListItem
                                    team={team}
                                    key={i}
                                    authState={authState!}
                                    isBusy={this.state.isBusy}
                                    isClickedOverrideCallback={this.isClickedOverrideCallback.bind(this)}
                                    teamToggleCallback={(checked: boolean, id: string) => this.teamToggle(checked, id)} />
                            }
                            )
                        }
                    </tbody>
                </table>
                <ConfirmSelection saveCallback={this.authSelectedTeams.bind(this)} 
                    authCount={this.state.teamIdsToAuth.length} 
                    deAuthCount={this.state.teamIdsToDeauth.length} 
                    isBusy={!this.state.isBusy} />
            </div>

        );
    }


    // 2nd stage load: auth-state for each team user has access to
    async updateTeamsAuthState() {
        var teamIds: Array<string> = [];

        this.setState({isBusy: true});

        // Get list of team Ids
        this.props.teamsList.forEach((team: any) => {
            teamIds.push(team.id);
        });

        // Is the ASP.Net API defined? If we're debugging from the react app only, it won't be.
        if (window.o365AnalyticsAuthAPI) {
            var response = await fetch(window.o365AnalyticsAuthAPI, {
                method: "POST",
                credentials: "same-origin",
                headers: {
                    'Content-Type': 'application/json',
                    'Accept': 'application/json'
                },
                body: JSON.stringify(teamIds)
                })
                .catch(err => {
                    this.showApiError(err);
                });

            if (response && response.ok) {
                const responseJson: Array<TeamAuthStatusResponse> = await response.json();

                // For existing authed teams, add for re-auth
                responseJson.forEach(r => {
                    if (r.hasAuthToken) {
                        this.state.teamIdsToAuth.push(r.teamId);
                        console.info(`Adding team ${r.teamId} for re-auth`);
                    }
                });

                // Build UI state
                const loadedAuthState : Array<TeamAuthStatus> = [];
                responseJson.forEach(teamAuthState => 
                {
                    if (teamAuthState.hasAuthToken) {
                        loadedAuthState.push({authStatus: AuthTokenResponse.HaveAuth, teamId: teamAuthState.teamId});
                    }
                    else
                        loadedAuthState.push({authStatus: AuthTokenResponse.NoAuth, teamId: teamAuthState.teamId});
                });

                // Set state for render
                this.setState({ userTeamsAuthState: loadedAuthState });
                this.setState({isBusy: false});
            }
        }
    }

    // Save config via REST call
    async authSelectedTeams() 
    { 
        this.setState({isBusy: true});

        var body =
        {
            "teamIdsToAuth": this.state.teamIdsToAuth,
            "teamIdsToDeauth": this.state.teamIdsToDeauth,
            "token": window.o365AnalyticsTeamsToken.accessToken
        };

        fetch(window.o365AnalyticsAuthAPI, {
            method: "PUT",
            credentials: "same-origin",
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify(body)})
            .then(response => {
                if (!response.ok)
                {
                    this.showApiError(response);
                }
                else
                {
                    toast('Selected Teams enabled for deep analytics successfully. It may take several hours before the extra metadata appears in any reports.');
                    window.location.reload();
                }   
                this.setState({isBusy: false});
            })
            .catch(err => {
                this.showApiError(err);
            });
    }

    showApiError(errOrResponse: any)
    {
        console.log(errOrResponse);
        toast.error('Unexpected response from API. Check JS log for more details.');
        this.setState({isBusy: false});
    }
}
