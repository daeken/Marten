import { useCallback, useLayoutEffect, useRef, useState, useSyncExternalStore } from 'react';
import { Api, PendingMessage } from './api';

type Draft = { text: string; recalling?: string; recallError?: string };
const emptyDraft: Draft = { text: '' };

// Keep drafts and in-flight recalls tied to the agent, not the mounted view. Navigation
// while a DELETE is in flight must not lose the recovered text or write it to another chat.
export class ComposerDrafts {
  private current = new Map<string, Draft>();
  private listeners = new Set<() => void>();

  subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  };
  read = (id: string) => this.current.get(id) ?? emptyDraft;
  private write(id: string, draft: Draft) {
    if (draft.text || draft.recalling || draft.recallError) this.current.set(id, draft);
    else this.current.delete(id);
    for (const listener of this.listeners) listener();
  }
  edit(id: string, text: string) {
    const draft = this.read(id);
    if (!draft.recalling) this.write(id, { ...draft, text });
  }
  async recall(api: Api, id: string, messageId: string) {
    const draft = this.read(id);
    if (draft.text || draft.recalling) return false;
    // Lock all draft writes, including keyboard send, synchronously until DELETE settles.
    this.write(id, { text: '', recalling: messageId });
    try {
      const removed = await api.recallPendingMessage(id, messageId);
      this.write(id, removed ? { text: removed.text } : { text: '', recallError: 'That message is no longer available to recall.' });
      return !!removed;
    } catch {
      this.write(id, { text: '', recallError: 'Couldn’t confirm recall. Check pending messages and the transcript before sending again.' });
      return false;
    }
  }
}

function usePendingMessages(api: Api, id: string, tick: number, recalling: boolean) {
  const [rows, setRows] = useState<PendingMessage[]>([]);
  const [loadError, setLoadError] = useState(false);
  const invalidate = useRef(() => {});
  useLayoutEffect(() => {
    let version = 0;
    let active = true;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let request: AbortController | undefined;
    const schedule = () => {
      if (!active || timer !== undefined || request) return;
      // Coalesce bursts to one request plus a trailing refresh; never commit a snapshot
      // superseded by an event, reconnect, or mutation while the request was in flight.
      timer = setTimeout(async () => {
        timer = undefined;
        const started = version;
        const controller = request = new AbortController();
        const deadline = setTimeout(() => controller.abort(), 10_000);
        try {
          const pending = await api.pendingMessages(id, controller.signal);
          if (active && started === version) {
            setRows(pending);
            setLoadError(false);
          }
        } catch {
          if (active && started === version) {
            setRows([]);
            setLoadError(true);
          }
        } finally {
          clearTimeout(deadline);
          request = undefined;
          if (started !== version) schedule();
        }
      }, 50);
    };
    invalidate.current = () => { ++version; schedule(); };
    return () => {
      active = false;
      clearTimeout(timer);
      request?.abort();
    };
  }, [api, id]);
  useLayoutEffect(() => { invalidate.current(); }, [api, id, tick]);
  useLayoutEffect(() => { if (!recalling) invalidate.current(); }, [recalling]);
  return { rows, loadError, refresh: () => invalidate.current() };
}

export function Composer({ api, id, busy, pendingTick, drafts }: {
  api: Api; id: string; busy: boolean; pendingTick: number; drafts: ComposerDrafts;
}) {
  const read = useCallback(() => drafts.read(id), [drafts, id]);
  const draft = useSyncExternalStore(drafts.subscribe, read);
  const { rows, loadError, refresh } = usePendingMessages(api, id, pendingTick, !!draft.recalling);
  const [mode, setMode] = useState<PendingMessage['mode']>('queued');
  const field = useRef<HTMLTextAreaElement>(null);
  const send = async () => {
    const latest = drafts.read(id);
    const t = latest.text.trim();
    if (latest.recalling || !t) return;
    drafts.edit(id, '');
    await api.send(id, t, busy ? mode : 'queued');
    refresh();
  };
  const recall = async (messageId: string) => {
    const recalled = await drafts.recall(api, id, messageId);
    refresh();
    if (recalled) {
      setMode('queued');
      field.current?.focus();
    }
  };
  return (
    <footer className="composer">
      {(rows.length > 0 || loadError || draft.recallError || draft.recalling) && <section className="pending-composer" aria-label="Pending messages">
        {rows.length > 0 && <ul aria-label="Pending messages" tabIndex={0}>
          {rows.map(row => <li key={row.id}>
            {row.mode === 'queued'
              ? <button className="pending-row" disabled={!!draft.text || !!draft.recalling} onClick={() => recall(row.id)}>
                <span className="muted">Queued · Edit</span><span className="pending-text">{row.text}</span>
              </button>
              : <div className="pending-row"><span className="muted">Steering</span><span className="pending-text">{row.text}</span></div>}
          </li>)}
        </ul>}
        {rows.some(row => row.mode === 'queued') && draft.text && <div className="muted small">Clear or send your draft to edit a queued message.</div>}
        {draft.recalling && <div role="status" className="small">Recalling message…</div>}
        {draft.recallError && <div role="status" className="small">{draft.recallError}</div>}
        {loadError && <div role="status" className="small">Couldn’t load pending messages. <button className="link small" onClick={refresh}>Retry</button></div>}
      </section>}
      <div className="composer-row">
        <textarea ref={field} aria-label="Message" value={draft.text} readOnly={!!draft.recalling}
          placeholder={busy ? 'Message (agent is working)' : 'Message'} onChange={e => drafts.edit(id, e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); } }} rows={3} />
        <div className="actions">
          <button className="primary" onClick={send} disabled={!draft.text.trim() || !!draft.recalling}>Send</button>
          {busy && <button onClick={() => api.interrupt(id)}>Stop</button>}
        </div>
      </div>
      {busy && <div className="modes">
        <label><input type="radio" disabled={!!draft.recalling} checked={mode === 'queued'} onChange={() => setMode('queued')} /> Queue</label>
        <label><input type="radio" disabled={!!draft.recalling} checked={mode === 'immediate'} onChange={() => setMode('immediate')} /> Steer now</label>
      </div>}
    </footer>
  );
}
