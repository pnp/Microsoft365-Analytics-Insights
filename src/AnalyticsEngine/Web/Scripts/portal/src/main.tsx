import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { HashRouter } from 'react-router-dom';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import App from './App';
import './index.css';

const container = document.getElementById('root');
if (!container) {
  throw new Error('Root container #root not found');
}

createRoot(container).render(
  <StrictMode>
    <FluentProvider theme={webLightTheme} style={{ minHeight: '100vh' }}>
      <HashRouter>
        <App />
      </HashRouter>
    </FluentProvider>
  </StrictMode>,
);
