import { useCallback, useEffect, useState } from 'react';
import { Api, Device, PairStatus } from './api';

export function DevicePairing({ api }: { api: Api }) {
  const [status, setStatus] = useState<PairStatus>();
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const load = useCallback(() => api.pairStatus().then(setStatus), [api]);
  useEffect(() => { void load().catch(() => {}); }, [load]);
  if (!status) return null;
  const run = async (action: () => Promise<unknown>) => {
    setError('');
    setBusy(true);
    try { await action(); }
    catch (e) { setError(e instanceof Error ? e.message : 'Pairing request failed.'); }
    finally { setBusy(false); }
  };
  const revoke = (device: Device) => {
    if (window.confirm(`Revoke access for ${device.name}?`)) void run(async () => {
      await api.revokeDevice(device.id);
      await load();
    });
  };
  return <details onToggle={e => { if (e.currentTarget.open) void run(load); }}>
    <summary>Paired devices</summary>
    <p className="small muted">Pair your device to control Marten.</p>
    <p className="small muted">{!status.configured ? 'Native transport is not configured.'
      : !status.valid ? 'The configured tailnet host is invalid.'
      : status.ready ? 'The server’s tailnet listener is ready.'
      : 'The tailnet host is configured, but the listener is not ready.'}</p>
    {!status.enrollmentAvailable && <p className="small muted">One-scan enrollment is not available yet.</p>}
    <button className="small" disabled={busy} onClick={() => void run(load)}>Refresh devices</button>
    {status.devices.length === 0 && <p className="small muted">No paired devices.</p>}
    {status.devices.map(d => <div className="row small" key={d.id}>
      <span title={new Date(d.createdAt).toLocaleString()}>{d.name}</span>
      <button className="small" disabled={busy} onClick={() => revoke(d)}>Revoke</button>
    </div>)}
    {error && <div className="error small" role="alert">{error}</div>}
  </details>;
}
