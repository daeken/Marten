import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { AgentSummary, Api, Entry, ServerEvent, Status_, takeToken } from './api';

const EFFORTS = ['low', 'medium', 'high', 'xhigh'];

/** Merge entries by sequence number, so a late HTTP snapshot never erases live websocket entries. */
function mergeEntries(a: Entry[], b: Entry[]): Entry[] {
  const bySeq = new Map<number, Entry>();
  for (const e of a) bySeq.set(e.seq, e);
  for (const e of b) bySeq.set(e.seq, e);
  return [...bySeq.values()].sort((x, y) => x.seq - y.seq);
}

const statusLabel: Record<AgentSummary['status'], string> = {
  idle: 'Idle', running: 'Working', waiting_for_quota: 'Waiting for quota', error: 'Error',
};

export function App() {
  const [token] = useState(takeToken);
  const api = useMemo(() => (token ? new Api(token) : null), [token]);
  const [status, setStatus] = useState<Status_ | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [transcripts, setTranscripts] = useState<Record<string, Entry[]>>({});
  const [streaming, setStreaming] = useState<Record<string, string>>({});
  const [running, setRunning] = useState<Record<string, string>>({});
  const [models, setModels] = useState<string[]>([]);
  const connected = status?.auth.connected ?? false;
  useEffect(() => {
    if (api && connected) api.models().then(setModels).catch(() => setModels([]));
  }, [api, connected]);

  const refresh = useCallback(async () => {
    if (!api) return;
    try {
      const s = await api.status();
      setStatus(s);
      setError(null);
      setSelected(cur => cur ?? s.agents[s.agents.length - 1]?.id ?? null);
    } catch (e) {
      setError((e as Error).message === 'unauthorized' ? 'unauthorized' : (e as Error).message);
    }
  }, [api]);

  const loadTranscript = useCallback(async (id: string) => {
    if (!api) return;
    const entries = await api.transcript(id);
    setTranscripts(t => ({ ...t, [id]: mergeEntries(t[id] ?? [], entries) }));
  }, [api]);

  useEffect(() => { refresh(); }, [refresh]);
  useEffect(() => { if (selected) loadTranscript(selected); }, [selected, loadTranscript]);

  // Keep the latest selection visible to the socket handler without resubscribing.
  const selectedRef = useRef(selected);
  selectedRef.current = selected;

  useEffect(() => {
    if (!api) return;
    const onEvent = (e: ServerEvent) => {
      switch (e.type) {
        case 'entry':
          setTranscripts(t => t[e.agentId] ? { ...t, [e.agentId]: mergeEntries(t[e.agentId], [e.entry]) } : t);
          if (e.entry.kind === 'assistant' || e.entry.kind === 'tool_call' || e.entry.kind === 'idle' || e.entry.kind === 'error')
            setStreaming(s => ({ ...s, [e.agentId]: '' }));
          if (e.entry.kind === 'tool_result') setRunning(r => { const { [e.agentId]: _, ...rest } = r; return rest; });
          break;
        case 'delta':
          setStreaming(s => ({ ...s, [e.agentId]: (s[e.agentId] ?? '') + e.text }));
          break;
        case 'tool_start':
          setRunning(r => ({ ...r, [e.agentId]: e.callId }));
          break;
        case 'status':
          setStatus(s => s && { ...s, agents: s.agents.map(a => a.id === e.agentId ? { ...a, status: e.status, statusDetail: e.detail } : a) });
          break;
        case 'inbox':
          setStatus(s => s && { ...s, agents: s.agents.map(a => a.id === e.agentId ? { ...a, pending: e.pending } : a) });
          break;
        case 'flag':
        case 'agents':
        case 'config':
          refresh();
          break;
      }
    };
    return api.events(onEvent, () => {
      refresh();
      if (selectedRef.current) loadTranscript(selectedRef.current);
    });
  }, [api, refresh, loadTranscript]);

  if (!token || error === 'unauthorized')
    return <div className="gate"><h1>Marten</h1><p>Open the link Marten printed when it started. It carries the token this page needs.</p></div>;
  if (!api || !status)
    return <div className="gate"><p>{error ?? 'Connecting…'}</p></div>;

  const agent = status.agents.find(a => a.id === selected) ?? null;

  return (
    <div className="shell">
      <Sidebar api={api} status={status} selected={selected} onSelect={setSelected} onChange={refresh} />
      <main className="main">
        {agent
          ? <AgentView api={api} agent={agent} models={models} defaultModel={status.config.model ?? status.config.defaultModel ?? models[0]} defaultEffort={status.config.effort} onChange={refresh} entries={transcripts[agent.id] ?? []} streaming={streaming[agent.id] ?? ''} runningCall={running[agent.id]} />
          : <div className="empty">Start a chat to begin.</div>}
      </main>
    </div>
  );
}

function Sidebar({ api, status, selected, onSelect, onChange }: {
  api: Api; status: Status_; selected: string | null; onSelect: (id: string) => void; onChange: () => void;
}) {
  const connect = async () => {
    const { url } = await api.login();
    window.open(url, '_blank', 'noopener');
    // The sign-in finishes on Marten's loopback callback; poll until it lands.
    for (let i = 0; i < 120; i++) {
      await new Promise(r => setTimeout(r, 2500));
      const s = await api.status();
      if (s.auth.connected || !s.auth.signingIn) break;
    }
    onChange();
  };

  const newChat = async () => {
    const name = prompt('Name this chat', 'Chat');
    if (name === null) return;
    const a = await api.createChat(name);
    onChange();
    onSelect(a.id);
  };

  return (
    <aside className="sidebar">
      <header className="brand">Marten</header>
      <section className="auth">
        {status.auth.connected ? (
          <>
            <div className="muted small">ChatGPT · {status.auth.email ?? 'connected'}</div>
            <button className="link" onClick={() => api.logout().then(onChange)}>Sign out</button>
          </>
        ) : (
          <>
            <button className="primary" onClick={connect}>
              {status.auth.signingIn ? 'Waiting for sign-in… (click to restart)' : 'Connect ChatGPT'}
            </button>
            {status.auth.lastError && <div className="error small">{status.auth.lastError}</div>}
          </>
        )}
      </section>
      <nav className="agents">
        <div className="row">
          <span className="muted small">Chats</span>
          <button className="link" onClick={newChat}>New</button>
        </div>
        {status.agents.map(a => (
          <button key={a.id} className={`agent ${a.id === selected ? 'active' : ''}`} onClick={() => onSelect(a.id)}>
            <span className={`dot ${a.status}`} title={statusLabel[a.status]} />
            <span className="name">{a.name}</span>
            {a.pending > 0 && <span className="badge">{a.pending}</span>}
            {a.abandonedRunspaces.length > 0 && <span className="badge warn" title="Abandoned runspaces">!</span>}
          </button>
        ))}
      </nav>
    </aside>
  );
}

function AgentView({ api, agent, models, defaultModel, defaultEffort, onChange, entries, streaming, runningCall }: {
  api: Api; agent: AgentSummary; models: string[]; defaultModel?: string; defaultEffort?: string; onChange: () => void;
  entries: Entry[]; streaming: string; runningCall?: string;
}) {
  const [text, setText] = useState('');
  const [mode, setMode] = useState<'queued' | 'immediate'>('queued');
  const scroller = useRef<HTMLDivElement>(null);
  const busy = agent.status === 'running' || agent.status === 'waiting_for_quota';

  useEffect(() => {
    const el = scroller.current;
    if (el && el.scrollHeight - el.scrollTop - el.clientHeight < 200) el.scrollTop = el.scrollHeight;
  }, [entries, streaming]);
  useEffect(() => { scroller.current?.scrollTo(0, scroller.current.scrollHeight); }, [agent.id]);

  const results = useMemo(() => new Map(entries.filter(e => e.kind === 'tool_result').map(e => [e.callId, e])), [entries]);

  const send = async () => {
    const t = text.trim();
    if (!t) return;
    setText('');
    await api.send(agent.id, t, busy ? mode : 'queued');
  };

  return (
    <div className="agent-view">
      <header className="agent-head">
        <div>
          <div className="title">{agent.name}</div>
          <div className="muted small">{agent.workingDirectory}</div>
        </div>
        <div className="head-right">
          <div className="pickers">
            <select title="Model for this agent" value={agent.model ?? ''} onChange={e => api.setAgentSettings(agent.id, e.target.value || null, agent.effort ?? null).then(onChange)}>
              <option value="">{defaultModel ? `Default (${defaultModel})` : 'Default model'}</option>
              {[...new Set([...(agent.model ? [agent.model] : []), ...models])].map(m => <option key={m} value={m}>{m}</option>)}
            </select>
            <select title="Reasoning effort for this agent" value={agent.effort ?? ''} onChange={e => api.setAgentSettings(agent.id, agent.model ?? null, e.target.value || null).then(onChange)}>
              <option value="">{defaultEffort ? `Default (${defaultEffort})` : 'Default effort'}</option>
              {EFFORTS.map(x => <option key={x} value={x}>{x}</option>)}
            </select>
          </div>
          <div className={`state ${agent.status}`}>{statusLabel[agent.status]}{agent.statusDetail ? ` · ${agent.statusDetail}` : ''}</div>
        </div>
      </header>
      {agent.abandonedRunspaces.length > 0 && (
        <div className="banner">
          {agent.abandonedRunspaces.length} PowerShell runspace{agent.abandonedRunspaces.length > 1 ? 's' : ''} abandoned after a timeout. Last: <code>{agent.abandonedRunspaces.at(-1)!.command.slice(0, 120)}</code>. Restart Marten to reclaim them.
        </div>
      )}
      <div className="log" ref={scroller}>
        {entries.map(e => <EntryView key={e.seq} entry={e} result={e.kind === 'tool_call' ? results.get(e.callId) : undefined} running={e.callId === runningCall} />)}
        {streaming && <div className="assistant streaming"><Md text={streaming} /></div>}
        {agent.status === 'running' && !streaming && !runningCall && <Thinking />}
        {agent.status === 'waiting_for_quota' && <Thinking label="Waiting for quota" />}
      </div>
      <footer className="composer">
        <div className="composer-row">
          <textarea
            value={text}
            placeholder={busy ? 'Message (agent is working)' : 'Message'}
            onChange={e => setText(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); } }}
            rows={3}
          />
          <div className="actions">
            <button className="primary" onClick={send} disabled={!text.trim()}>Send</button>
            {busy && <button onClick={() => api.interrupt(agent.id)}>Stop</button>}
          </div>
        </div>
        {busy && (
          <div className="modes">
            <label><input type="radio" checked={mode === 'queued'} onChange={() => setMode('queued')} /> Queue</label>
            <label><input type="radio" checked={mode === 'immediate'} onChange={() => setMode('immediate')} /> Steer now</label>
          </div>
        )}
      </footer>
    </div>
  );
}

function EntryView({ entry: e, result, running }: { entry: Entry; result?: Entry; running: boolean }) {
  switch (e.kind) {
    case 'message':
      return e.from === 'Sera'
        ? <div className="message mine">{e.text}</div>
        : <div className="message other"><div className="from">{e.from}</div><Md text={e.text ?? ''} /></div>;
    case 'assistant':
      return e.text ? <div className="assistant"><Md text={e.text} /></div> : null;
    case 'reasoning':
      return e.text ? <details className="reasoning"><summary>Reasoning</summary><div><Md text={e.text} /></div></details> : null;
    case 'tool_call':
      return <ToolCall call={e} result={result} running={running} />;
    case 'error':
      return <div className="error-entry">{e.text}</div>;
    case 'idle':
      return e.reason === 'interrupted' ? <div className="divider">Interrupted</div> : null;
    case 'wake':
      return e.reason === 'restart' ? <div className="divider">Resumed after restart</div> : null;
    default:
      return null;
  }
}

function ToolCall({ call, result, running }: { call: Entry; result?: Entry; running: boolean }) {
  const args = useMemo(() => { try { return JSON.parse(call.arguments ?? '{}'); } catch { return call.arguments; } }, [call.arguments]);
  const summary = call.name === 'powershell' ? args?.command : args?.path ?? '';
  const state = result ? (result.isError ? 'failed' : 'done') : running ? 'running' : 'pending';
  return (
    <details className={`tool ${state}`}>
      <summary>
        <span className="tool-name">{call.name}</span>
        <span className="tool-summary">{String(summary ?? '').split('\n')[0].slice(0, 160)}</span>
        <span className="tool-state">{state === 'running' ? 'running…' : state === 'failed' ? 'error' : ''}</span>
      </summary>
      <pre className="args">{typeof args === 'string' ? args : call.name === 'powershell' ? args.command : JSON.stringify(args, null, 2)}</pre>
      {result && <pre className="output">{result.text}</pre>}
    </details>
  );
}

/** Markdown without raw HTML: react-markdown escapes HTML unless a plugin allows it, so model output can't inject markup. */
function Md({ text }: { text: string }) {
  return (
    <div className="md">
      <Markdown remarkPlugins={[remarkGfm]} components={{ a: ({ node: _, ...props }) => <a {...props} target="_blank" rel="noreferrer" /> }}>
        {text}
      </Markdown>
    </div>
  );
}

function Thinking({ label = 'Thinking' }: { label?: string }) {
  return (
    <div className="thinking" aria-live="polite">
      <span className="blob" /><span className="blob" /><span className="blob" />
      <span className="label">{label}</span>
    </div>
  );
}
