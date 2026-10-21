import { Button, Tooltip } from '@fluentui/react-components';
import { PersonSearch20Regular } from '@fluentui/react-icons';
import { useInRouterContext, useNavigate } from 'react-router-dom';
import { usePortalAccess } from '../../access/PortalAccessProvider';
import type { UserDiagnosticsContext } from '../../types/userDiagnostics';
import { useT } from '../../i18n';

/** Where User lookup lives; the diagnostics read their report context from the navigation state. */
export const USER_LOOKUP_PATH = '/admin/user-lookup';

function Link({ context }: { context: UserDiagnosticsContext }) {
  const t = useT();
  const navigate = useNavigate();
  return (
    <Tooltip relationship="description" content={t('userDiagnostics.troubleshootHint')}>
      <Button
        size="small"
        appearance="secondary"
        icon={<PersonSearch20Regular />}
        // The person and the report's period travel in router state, not the URL, so a UPN never lands in
        // browser history or a server log.
        onClick={() => navigate(USER_LOOKUP_PATH, { state: { diagnostics: context } })}
      >
        {t('userDiagnostics.troubleshoot')}
      </Button>
    </Tooltip>
  );
}

/**
 * "Troubleshoot this person": opens User lookup's licence diagnostics with this report's context. Shown only to
 * readers who could open that page (Administration and See PII), and only inside the portal's router.
 */
export default function TroubleshootUserLink({ context }: { context: UserDiagnosticsContext }) {
  const access = usePortalAccess();
  const inRouter = useInRouterContext();
  if (!inRouter || !access.administration || !access.seePii) return null;
  return <Link context={context} />;
}
