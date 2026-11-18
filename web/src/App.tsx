import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { AgentSummary, Api, Entry, Project, ProjectMemory, SeraMessage, ServerEvent, Status_, TaskItem, takeToken } from './api';

const EFFORTS = ['low', 'medium', 'high', 'xhigh'];

/** "Name (id)" labels from the runtime, shown as just the name. */
const shortFrom = (from?: string) => (from ?? '').replace(/ \([^)]*\)$/, '');
const time = (iso: string) => new Date(iso).toLocaleString([], { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });

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
  /** Bumped on task and memory events so open panels refetch. */
  const [tick, setTick] = useState(0);
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
      setSelected(cur => cur ?? s.agents[s.agents.length - 1]?.id ?? 'new:project');
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
  useEffect(() => { if (selected && !selected.includes(':') && selected !== 'inbox') loadTranscript(selected); }, [selected, loadTranscript]);

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
        case 'tasks':
        case 'memory':
          setTick(t => t + 1);
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
        case 'projects':
        case 'sera_message':
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
  const project = agent ? status.projects.find(p => p.id === agent.projectId) : undefined;
  let main;
  if (agent)
    main = <AgentView api={api} agent={agent} project={project} agents={status.agents} models={models} tick={tick} defaultModel={status.config.model ?? status.config.defaultModel ?? models[0]} defaultEffort={status.config.effort} onChange={refresh} onSelect={setSelected} entries={transcripts[agent.id] ?? []} streaming={streaming[agent.id] ?? ''} runningCall={running[agent.id]} />;
  else if (selected === 'inbox')
    main = <InboxView api={api} status={status} tick={status.inbox} onSelect={setSelected} />;
  else if (selected?.startsWith('memory:'))
    main = <MemoryView api={api} project={status.projects.find(p => p.id === selected.slice(7))} agents={status.agents} tick={tick} />;
  else if (selected === 'new:project')
    main = <NewProject api={api} onCreated={id => { refresh(); setSelected(id); }} />;
  else
    main = <div className="empty">Start a chat or a project to begin.</div>;

  return (
    <div className="shell">
      <Sidebar api={api} status={status} selected={selected} onSelect={setSelected} onChange={refresh} />
      <main className="main">
        {main}
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
        <button className={`agent inbox ${selected === 'inbox' ? 'active' : ''}`} onClick={() => onSelect('inbox')}>
          <span className="name">Inbox</span>
          {status.inbox > 0 && <span className="badge">{status.inbox}</span>}
        </button>
        <div className="row">
          <span className="muted small">Projects</span>
          <button className="link" onClick={() => onSelect('new:project')}>New</button>
        </div>
        {status.projects.map(p => {
          const members = status.agents.filter(a => a.projectId === p.id);
          const orch = members.filter(a => a.kind === 'orchestrator');
          const tasks = members.filter(a => a.kind === 'task');
          return (
            <div key={p.id} className="project">
              <div className="project-head">
                <span className="project-name" title={p.bindings.join('\n')}>{p.name}</span>
                <button className={`link small ${selected === `memory:${p.id}` ? 'on' : ''}`} onClick={() => onSelect(`memory:${p.id}`)}>Memory</button>
              </div>
              {[...orch, ...tasks].map(a => <AgentRow key={a.id} agent={a} selected={selected} onSelect={onSelect} nested={a.kind === 'task'} />)}
            </div>
          );
        })}
        <div className="row">
          <span className="muted small">Chats</span>
          <button className="link" onClick={newChat}>New</button>
        </div>
        {status.agents.filter(a => a.kind === 'chat').map(a => <AgentRow key={a.id} agent={a} selected={selected} onSelect={onSelect} />)}
      </nav>
    </aside>
  );
}

function AgentRow({ agent: a, selected, onSelect, nested }: { agent: AgentSummary; selected: string | null; onSelect: (id: string) => void; nested?: boolean }) {
  return (
    <button className={`agent ${a.id === selected ? 'active' : ''} ${nested ? 'nested' : ''} ${a.closed ? 'closed' : ''}`} onClick={() => onSelect(a.id)}>
      <span className={`dot ${a.status}`} title={a.closed ? 'Closed' : statusLabel[a.status]} />
      <span className="name">{a.name}</span>
      {a.openTasks > 0 && <span className="badge" title="Open tasks">{a.openTasks}</span>}
      {a.pending > 0 && <span className="badge" title="Queued messages">✉ {a.pending}</span>}
      {(a.abandonedRunspaces.length > 0 || a.nudgesStopped) && <span className="badge warn" title={a.nudgesStopped ? 'Nudges stopped' : 'Abandoned runspaces'}>!</span>}
    </button>
  );
}

function AgentView({ api, agent, project, agents, models, tick, defaultModel, defaultEffort, onChange, onSelect, entries, streaming, runningCall }: {
  api: Api; agent: AgentSummary; project?: Project; agents: AgentSummary[]; models: string[]; tick: number;
  defaultModel?: string; defaultEffort?: string; onChange: () => void; onSelect: (id: string) => void;
  entries: Entry[]; streaming: string; runningCall?: string;
}) {
  const [tasks, setTasks] = useState<TaskItem[]>([]);
  useEffect(() => { api.tasks(agent.id).then(setTasks).catch(() => setTasks([])); }, [api, agent.id, tick]);
  const parent = agents.find(a => a.id === agent.parentId);
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
          <div className="title">
            {project && <span className="crumb">{project.name} / </span>}
            {agent.name}
            {agent.closed && <span className="tag">closed</span>}
          </div>
          <div className="muted small">
            {agent.worktree ?? agent.workingDirectory}
            {parent && <> · reports to <button className="link small" onClick={() => onSelect(parent.id)}>{parent.name}</button></>}
          </div>
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
      {tasks.length > 0 && (
        <div className="tasks">
          {tasks.map(t => (
            <span key={t.id} className={`task ${t.state}`} title={t.reason ?? ''}>
              <span className="task-state">{t.state}</span> #{t.id} {t.title}{t.reason && t.state !== 'open' ? ` · ${t.reason}` : ''}
            </span>
          ))}
        </div>
      )}
      {agent.nudgesStopped && (
        <div className="banner">Nudges stopped: this agent went idle three times with open tasks and nothing changed. Message it to get it going again.</div>
      )}
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
        : e.from === 'Marten'
          ? <div className="message system"><Md text={e.text ?? ''} /></div>
          : <div className="message other"><div className="from">{shortFrom(e.from)}</div><Md text={e.text ?? ''} /></div>;
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
      return e.reason === 'restart' ? <div className="divider">Resumed after restart</div>
        : e.reason === 'nudge' ? <div className="divider">Nudged: open tasks</div> : null;
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

function NewProject({ api, onCreated }: { api: Api; onCreated: (orchestratorId: string) => void }) {
  const [name, setName] = useState('');
  const [bindings, setBindings] = useState('');
  const [prompt, setPrompt] = useState('');
  const [brief, setBrief] = useState('');
  const [worktrees, setWorktrees] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const p = await api.createProject({
        name, bindings: bindings.split('\n').map(b => b.trim()).filter(Boolean),
        prompt: prompt.trim() || null, worktreesByDefault: worktrees, brief: brief.trim() || null,
      });
      onCreated(p.orchestratorId!);
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="page">
      <form className="form" onSubmit={submit}>
        <h2>New project</h2>
        <label>Name<input value={name} onChange={e => setName(e.target.value)} placeholder="Open Thedas" required /></label>
        <label>Bound folders or repos, one per line
          <textarea value={bindings} onChange={e => setBindings(e.target.value)} rows={3} placeholder="/Volumes/External2/projects/OpenThedas" required />
          <span className="hint">The first one is the orchestrator's working directory. AGENTS.md at each root is read.</span>
        </label>
        <label>Project prompt
          <textarea value={prompt} onChange={e => setPrompt(e.target.value)} rows={5} placeholder="Goals, rules and context every agent in this project should know." />
        </label>
        <label className="check"><input type="checkbox" checked={worktrees} onChange={e => setWorktrees(e.target.checked)} /> Give task agents their own git worktree by default</label>
        <label>First message to the orchestrator
          <textarea value={brief} onChange={e => setBrief(e.target.value)} rows={3} placeholder="Optional. What should it start on?" />
        </label>
        {error && <div className="error">{error}</div>}
        <div className="form-actions"><button className="primary" disabled={busy || !name.trim() || !bindings.trim()}>Create project</button></div>
      </form>
    </div>
  );
}

function MemoryView({ api, project, agents, tick }: { api: Api; project?: Project; agents: AgentSummary[]; tick: number }) {
  const [memory, setMemory] = useState<ProjectMemory | null>(null);
  useEffect(() => { if (project) api.memory(project.id).then(setMemory).catch(() => setMemory(null)); }, [api, project, tick]);
  if (!project) return <div className="empty">No such project.</div>;
  const name = (id: string) => agents.find(a => a.id === id)?.name ?? id;
  return (
    <div className="page">
      <div className="doc">
        <h2>{project.name} · memory</h2>
        <div className="muted small">{memory?.path}</div>
        <div className="memory">{memory?.text.trim() ? <Md text={memory.text} /> : <span className="muted">Empty so far.</span>}</div>
        {memory?.prompt && (<><h3>Project prompt</h3><div className="memory"><Md text={memory.prompt} /></div></>)}
        <h3>History</h3>
        {memory && memory.log.length === 0 && <div className="muted small">No edits yet.</div>}
        <ul className="history">
          {memory?.log.slice().reverse().map((e, i) => (
            <li key={i}>
              <div className="small"><strong>{name(e.agentId)}</strong> <span className="muted">{time(e.at)}{e.reason ? ` · ${e.reason}` : ''}</span></div>
              {e.removed.map((l, j) => <div key={`r${j}`} className="diff del">− {l}</div>)}
              {e.added.map((l, j) => <div key={`a${j}`} className="diff add">+ {l}</div>)}
            </li>
          ))}
        </ul>
      </div>
    </div>
  );
}

function InboxView({ api, status, tick, onSelect }: { api: Api; status: Status_; tick: number; onSelect: (id: string) => void }) {
  const [messages, setMessages] = useState<SeraMessage[]>([]);
  useEffect(() => { api.inbox().then(setMessages).catch(() => setMessages([])); }, [api, tick]);
  return (
    <div className="page">
      <div className="doc">
        <h2>Inbox</h2>
        {messages.length === 0 && <div className="muted">Nothing from your agents yet. Orchestrators write here when they need you.</div>}
        {messages.map(m => {
          const project = status.projects.find(p => p.id === m.projectId);
          return (
            <div key={m.id} className="inbox-item">
              <div className="small">
                <button className="link" onClick={() => onSelect(m.from)}>{project ? `${project.name} / ` : ''}{m.fromName}</button>
                <span className="muted"> · {time(m.at)}</span>
              </div>
              <Md text={m.text} />
            </div>
          );
        })}
      </div>
    </div>
  );
}
