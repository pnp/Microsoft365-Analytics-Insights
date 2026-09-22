import React from 'react';
import { TableRow, TableCell, TableCellLayout, Checkbox, Text } from '@fluentui/react-components';
import { CheckmarkCircle20Filled, Circle20Regular } from '@fluentui/react-icons';
import { tokens } from '@fluentui/react-components';
import { TeamAuthStatus, AuthTokenResponse } from '../../types/TeamAuthStatus';
import type { Team } from '@microsoft/microsoft-graph-types';
import { useT, type TFunction } from '../../i18n';

type TeamSelectionProps = {
  authState: TeamAuthStatus;
  teamToggleCallback: (checked: boolean, id: string | undefined) => void;
  isClickedOverrideCallback: (team: Team) => boolean;
  team: Team;
  isBusy: boolean;
  t: TFunction;
};

class TeamListItemInner extends React.Component<TeamSelectionProps> {
  render() {
    const checked: boolean = this.props.isClickedOverrideCallback(this.props.team);
    const authState = this.props.authState;
    const t = this.props.t;
    return (
      <TableRow>
        <TableCell>
          <TableCellLayout>
            <Checkbox
              checked={checked}
              disabled={this.props.isBusy}
              onChange={(_e: any, data: any) =>
                this.props.teamToggleCallback(data.checked === true, this.props.team.id ?? undefined)
              }
              label={this.props.team.displayName ?? t('admin.teams.teamListItem.unnamedTeam')}
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
              <CheckmarkCircle20Filled
                primaryFill={tokens.colorPaletteGreenForeground1}
                aria-label={t('admin.teams.teamListItem.authorised')}
              />
            ) : (
              <Circle20Regular aria-label={t('admin.teams.teamListItem.notAuthorised')} />
            )
          ) : (
            <Text>--</Text>
          )}
        </TableCell>
      </TableRow>
    );
  }
}

export default function TeamListItem(props: Omit<TeamSelectionProps, 't'>) {
  const t = useT();
  return <TeamListItemInner {...props} t={t} />;
}
