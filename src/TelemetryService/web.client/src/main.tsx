import { StrictMode, type ReactNode } from 'react'
import { createRoot } from 'react-dom/client'
import { FluentProvider, MessageBar, MessageBarBody, MessageBarTitle, webLightTheme } from '@fluentui/react-components'
import './index.css'
import App from './App.tsx'
import { initializeDashboardAuth } from './auth.ts'

const root = createRoot(document.getElementById('root')!)

function render(children: ReactNode) {
  root.render(
    <StrictMode>
      <FluentProvider theme={webLightTheme}>{children}</FluentProvider>
    </StrictMode>,
  )
}

void initializeDashboardAuth()
  .then((auth) => {
    render(<App auth={auth} />)
  })
  .catch((error: unknown) => {
    const message = error instanceof Error ? error.message : 'Authentication failed.'
    render(
      <div style={{ padding: '24px', maxWidth: '640px', margin: '0 auto' }}>
        <MessageBar intent="error">
          <MessageBarBody>
            <MessageBarTitle>Could not sign in</MessageBarTitle>
            {message}
          </MessageBarBody>
        </MessageBar>
      </div>,
    )
  })
