import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import './styles.css';

createRoot(document.getElementById('root')!).render(<StrictMode><App /></StrictMode>);

// Installable app: the service worker caches the shell (never the API). It needs a secure context:
// localhost, or HTTPS such as `tailscale serve`.
if ('serviceWorker' in navigator && window.isSecureContext && !import.meta.env.DEV)
  navigator.serviceWorker.register('/sw.js').catch(() => { /* not fatal: the app works without it */ });
