import { useEffect, useRef, useState } from 'react';
import { Api, JevStatus } from './api';

export function JevSettings({ api }: { api: Api }) {
  const [settings, setSettings] = useState<JevStatus>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const field = useRef<HTMLInputElement>(null);
  useEffect(() => {
    const request = new AbortController();
    const clear = () => { if (field.current) field.current.value = ''; };
    window.addEventListener('pagehide', clear);
    api.jevStatus(request.signal).then(setSettings).catch(() => {
      if (!request.signal.aborted) setError('Jev settings are available only in the local Marten web UI.');
    });
    return () => {
      request.abort();
      clear();
      window.removeEventListener('pagehide', clear);
    };
  }, [api]);

  async function update(action: () => Promise<JevStatus>, operation: 'save' | 'options' | 'remove') {
    setBusy(true);
    setError('');
    setNotice('');
    try {
      setSettings(await action());
      if (operation === 'remove') setNotice('Removed from Marten. Removing it here does not revoke it at TypeSafe.');
    } catch {
      setError(operation === 'remove'
        ? 'Local removal could not be confirmed. Removing it here does not revoke it at TypeSafe.'
        : 'Could not save Jev settings. Use a valid key and check that local settings storage is available.');
      try { setSettings(await api.jevStatus()); } catch { setSettings(undefined); }
    } finally {
      setBusy(false);
    }
  }

  return <section className="jev-settings" aria-label="Typesafe AI Jev">
    <div>Typesafe AI Jev</div>
    <p className="muted small">Get an API key in the <a href="https://console.typesafe.ai/keys" target="_blank" rel="noreferrer">TypeSafe console</a> and paste it here, not in chat. Marten does not check it with TypeSafe.</p>
    {settings && <>
      <div className="muted small">{!settings.storageHealthy ? 'Local settings unavailable · off' : settings.hasKey ? 'Key set' : 'No key set'}</div>
      <form autoComplete="off" onSubmit={e => {
        e.preventDefault();
        if (busy || !field.current) return;
        const key = field.current.value;
        field.current.value = '';
        void update(() => api.setJevKey(key), 'save');
      }}>
        <input ref={field} type="password" aria-label="Jev API key" autoComplete="off" spellCheck={false}
          autoCapitalize="none" disabled={busy} required />
        <button className="link small" disabled={busy}>{settings.hasKey ? 'Replace key' : 'Set key'}</button>
      </form>
      <button className="link small" type="button" disabled={busy || (settings.storageHealthy && !settings.hasKey)}
        onClick={() => {
          if (field.current) field.current.value = '';
          void update(async () => (await api.removeJevKey()).settings, 'remove');
        }}>Remove key</button>
      <div><label className="small"><input type="checkbox" checked={settings.enabled} disabled={busy || !settings.hasKey}
        onChange={e => void update(() => api.setJevOptions(e.target.checked, settings.imagePreviewEnabled), 'options')} /> Enable Jev</label></div>
      <div><label className="small"><input type="checkbox" checked={settings.imagePreviewEnabled} disabled={busy || !settings.hasKey}
        onChange={e => void update(() => api.setJevOptions(settings.enabled, e.target.checked), 'options')} /> Allow image preview API (opt-in)</label></div>
      <p className="muted small">These settings do not make provider calls. Once agent dispatch is wired, enabling Jev makes it available to all agents without per-call approval. Explicitly requested payloads go to TypeSafe; images require the separate preview opt-in.</p>
    </>}
    {error && <div className="error small" role="alert">{error}</div>}
    {notice && <div className="muted small" role="status">{notice}</div>}
  </section>;
}
