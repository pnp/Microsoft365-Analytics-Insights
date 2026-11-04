import { Component, type ReactNode } from 'react';
import { Button, MessageBar, MessageBarActions, MessageBarBody, MessageBarTitle, Text } from '@fluentui/react-components';
import { ArrowClockwise16Regular } from '@fluentui/react-icons';
import { useT } from '../i18n';

/**
 * What the reader sees in place of a page that failed: what happened, a way to try again, and the error's own
 * text to pass on. The header and navigation around it keep working, so any other page is still one click away.
 */
function RouteFailure({ error }: { error: unknown }) {
  const t = useT();
  const detail = error instanceof Error && error.message ? error.message : null;
  return (
    <MessageBar intent="error" layout="multiline" role="alert">
      <MessageBarBody>
        <MessageBarTitle>{t('app.routeError.title')}</MessageBarTitle>
        {t('app.routeError.body')}
        {detail && (
          <Text as="p" size={200} block>
            {t('app.routeError.detail', { message: detail })}
          </Text>
        )}
      </MessageBarBody>
      <MessageBarActions>
        {/* A real reload: it fetches the current index.html, and with it the current page chunks. */}
        <Button appearance="primary" icon={<ArrowClockwise16Regular />} onClick={() => window.location.reload()}>
          {t('app.routeError.reload')}
        </Button>
      </MessageBarActions>
    </MessageBar>
  );
}

interface RouteErrorBoundaryProps {
  /** The route on screen. A change clears a caught failure, so moving to another page shows that page. */
  resetKey: string;
  children: ReactNode;
}

interface RouteErrorBoundaryState {
  failure: { error: unknown } | null;
  resetKey: string;
}

/**
 * Catches a page that fails to render, so it costs the reader that page and not the whole portal.
 *
 * Without it, one render-time exception in any page - or a page chunk that still fails after `lazyWithReload`'s one
 * automatic reload - unmounts the React root: a white screen with no header, navigation, language picker or Sign out,
 * and nothing on screen to quote to support. It wraps only the routed page, inside the shell.
 */
export default class RouteErrorBoundary extends Component<RouteErrorBoundaryProps, RouteErrorBoundaryState> {
  constructor(props: RouteErrorBoundaryProps) {
    super(props);
    this.state = { failure: null, resetKey: props.resetKey };
  }

  static getDerivedStateFromError(error: unknown): Partial<RouteErrorBoundaryState> {
    return { failure: { error } };
  }

  static getDerivedStateFromProps(props: RouteErrorBoundaryProps, state: RouteErrorBoundaryState): Partial<RouteErrorBoundaryState> | null {
    return props.resetKey === state.resetKey ? null : { failure: null, resetKey: props.resetKey };
  }

  render() {
    return this.state.failure ? <RouteFailure error={this.state.failure.error} /> : this.props.children;
  }
}
