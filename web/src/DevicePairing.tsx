import { useCallback, useEffect, useRef, useState } from 'react';
import QRCode from 'qrcode';
import { Api, Device, PairInvitation, PairStatus } from './api';

export function DevicePairing({ api }: { api: Api }) {
  const [status, setStatus] = useState<PairStatus>();
  const [invitation, setInvitation] = useState<PairInvitation>();
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const canvas = useRef<HTMLCanvasElement>(null);
  const load = useCallback(() => api.pairStatus().then(setStatus), [api]);
  // The management API independently checks the operator token and actual local Host.
  useEffect(() => { void load().catch(() => {}); }, [load]);
  useEffect(() => {
    if (!invitation || !canvas.current) return;
    void QRCode.toCanvas(canvas.current, invitation.uri, { width: 240, margin: 4 }).catch(() => {
      setError('Could not draw the QR code.');
      setInvitation(undefined);
    });
    const timer = setTimeout(() => setInvitation(undefined), Math.max(0, Date.parse(invitation.expiresAt) - Date.now()));
    return () => clearTimeout(timer);
  }, [invitation]);
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
  return <details onToggle={e => { if (!e.currentTarget.open) setInvitation(undefined); }}>
    <summary>Paired devices</summary>
    <p className="small muted">Pair only devices you trust to control Marten and view its activity.</p>
    {status.enabled ? <button className="small" disabled={busy || !!invitation}
      onClick={() => void run(async () => setInvitation(await api.createPairCode()))}>Show pairing QR</button>
      : <p className="small muted">No trusted HTTPS pairing origin is configured.</p>}
    {invitation && <div>
      <canvas ref={canvas} style={{ maxWidth: '100%', height: 'auto' }} aria-label="Marten device pairing QR code" />
      <p className="small muted">Scan with the Marten app. One use; expires at {new Date(invitation.expiresAt).toLocaleTimeString()}.</p>
      <button className="small" onClick={() => setInvitation(undefined)}>Hide QR</button>
    </div>}
    <button className="small" disabled={busy} onClick={() => void run(load)}>Refresh devices</button>
    {status.devices.length === 0 && <p className="small muted">No paired devices.</p>}
    {status.devices.map(d => <div className="row small" key={d.id}>
      <span title={new Date(d.createdAt).toLocaleString()}>{d.name}</span>
      <button className="small" disabled={busy} onClick={() => revoke(d)}>Revoke</button>
    </div>)}
    {error && <div className="error small" role="alert">{error}</div>}
  </details>;
}
