import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { initializeDashboardAuth } from './auth.ts'

const root = createRoot(document.getElementById('root')!)

void initializeDashboardAuth()
  .then((auth) => {
    root.render(
      <StrictMode>
        <App auth={auth} />
      </StrictMode>,
    )
  })
  .catch((error: unknown) => {
    const message = error instanceof Error ? error.message : 'Authentication failed.'
    root.render(
      <div className="dashboard">
        <div className="error">
          <strong>Could not sign in:</strong> {message}
        </div>
      </div>,
    )
  })
