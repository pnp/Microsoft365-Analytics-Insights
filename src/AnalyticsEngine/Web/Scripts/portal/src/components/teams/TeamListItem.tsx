import React from 'react';
import { TableRow, TableCell, TableCellLayout, Checkbox, Text } from '@fluentui/react-components';
import { CheckmarkCircle20Filled, Circle20Regular } from '@fluentui/react-icons';
import { tokens } from '@fluentui/react-components';
import { TeamAuthStatus, AuthTokenResponse } from '../../types/TeamAuthStatus';
import type { Team } from '@microsoft/microsoft-graph-types';

type TeamSelectionProps = {
  authState: TeamAuthStatus;
  teamToggleCallback: (checked: boolean, id: string | undefined) => void;
  isClickedOverrideCallback: (team: Team) => boolean;
  team: Team;
  isBusy: boolean;
};

export default class TeamListItem extends React.Component<TeamSelectionProps> {
  render() {
    const checked: boolean = this.props.isClickedOverrideCallback(this.props.team);
    const authState = this.props.authState;
    return (
      <TableRow>
        <TableCell>
          <TableCellLayout>
            <Checkbox
              checked={checked}
              disabled={this.props.isBusy}
              onChange={(_e, data) =>
                this.props.teamToggleCallback(data.checked === true, this.props.team.id ?? undefined)
              }
              label={this.props.team.displayName ?? '(unnamed team)'}
            />
          </TableCellLayout>
        </TableCell>
        <TableCell>
          <Text font="monospace" size={200}>
            {this.props.team.id}
          </Text>
        </TableCell>
        <TableCell>
          {authState && authState.authStatus !== AuthTokenResponse.Unknown ? (
            authState.authStatus === AuthTokenResponse.HaveAuth ? (
              <CheckmarkCircle20Filled primaryFill={tokens.colorPaletteGreenForeground1} aria-label="Authorised" />
            ) : (
              <Circle20Regular aria-label="Not authorised" />
            )
          ) : (
            <Text>--</Text>
          )}
        </TableCell>
      </TableRow>
    );
  }
}
