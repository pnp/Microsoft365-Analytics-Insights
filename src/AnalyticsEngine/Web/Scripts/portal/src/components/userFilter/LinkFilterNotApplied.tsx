import { MessageBar, MessageBarBody, MessageBarTitle } from '@fluentui/react-components';
import { useT } from '../../i18n';
import { usePortalAccess } from '../../access';
import { useGlobalFilter } from '../globalFilter/GlobalFilterProvider';
import type { UserFilter } from '../../types/userFilter';
import { isEmptyFilter } from './userFilterModel';

/**
 * Tells a reader without See PII that the people filter in the address they opened - typically a colleague's shared
 * link - was not applied, and whom the figures describe instead.
 *
 * Narrowing a report by people needs See PII, so the page quietly drops the filter for this reader. Said nowhere, a
 * link to "EMEA Sales" would open on the figures for everyone and read as EMEA Sales'. The population named here is
 * the one the server applied: the administrator's report filter where one applies, otherwise everyone the report covers.
 */
export default function LinkFilterNotApplied({ requested }: { requested: UserFilter | null | undefined }) {
  const t = useT();
  const { seePii, roles } = usePortalAccess();
  const withinGlobalFilter = useGlobalFilter().effective?.applied === true;
  if (seePii || isEmptyFilter(requested)) return null;
  return (
    <MessageBar intent="warning" layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>{t('userFilter.linkNotApplied.title')}</MessageBarTitle>
        {t(withinGlobalFilter ? 'userFilter.linkNotApplied.bodyWithinGlobal' : 'userFilter.linkNotApplied.body', { role: roles.seePii })}
      </MessageBarBody>
    </MessageBar>
  );
}
