import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { HashRouter } from 'react-router-dom';
import App from './App';
import { initializeAuth } from './auth/auth-utils';
import './index.css';

// MSAL must be initialised before any component uses it (v3+ requirement). We render
// regardless of the outcome because the app also works via the server-side token path.
initializeAuth()
  .catch((err) => console.error('MSAL initialise failed', err))
  .finally(() => {
    const container = document.getElementById('root');
    if (!container) {
      throw new Error('Root container #root not found');
    }
    createRoot(container).render(
      <StrictMode>
        <HashRouter>
          <App />
        </HashRouter>
      </StrictMode>,
    );
  });
