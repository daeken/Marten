import { memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { AgentMessage, AgentSummary, Api, Backend, BackgroundProcess, BackupStatus, Entry, NotebookCell, OutsideClient, McpServer, Project, ProjectMemory, Schedule, ScriptToolInfo, SeraMessage, ServerEvent, Status_, TaskItem, takeToken, TranscriptPage, transcriptPageSize } from './api';

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

const isAgentId = (id: string) => !id.includes(':') && !['inbox', 'processes', 'messages', 'settings'].includes(id);

type ModelCall = AgentSummary['modelCall'];
const latestCall = (current: ModelCall, incoming: ModelCall): ModelCall =>
  current && (!incoming || current.id > incoming.id || (current.id === incoming.id && current.endedAt)) ? current : incoming;

const statusLabel: Record<AgentSummary['status'], string> = {
  idle: 'Idle', running: 'Working', waiting_for_quota: 'Waiting for quota', waiting_for_network: 'Waiting for network', waiting_for_slot: 'Waiting for slot', paused: 'Paused', error: 'Error',
};

export function App() {
  const [token] = useState(takeToken);
  const api = useMemo(() => (token ? new Api(token) : null), [token]);
  const [status, setStatus] = useState<Status_ | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  /** Phones show the sidebar as a drawer. */
  const [navOpen, setNavOpen] = useState(false);
  const [tail, setTail] = useState<(TranscriptPage & { id: string }) | null>(null);
  const [tailError, setTailError] = useState<{ id: string; message: string } | null>(null);
  const transcriptRequest = useRef(0);
  const selectedRef = useRef(selected);
  selectedRef.current = selected;
  const [streaming, setStreaming] = useState<Record<string, string>>({});
  const [running, setRunning] = useState<Record<string, string>>({});
  const [models, setModels] = useState<string[]>([]);
  /** Bumped on task and memory events so open panels refetch. */
  const [tick, setTick] = useState(0);
  /** Changes whenever a backend connects or disconnects, so the model list refetches. */
  const connected = status ? [status.auth.connected, ...status.backends.map(b => b.connected)].map(Number).join('') : '';
  useEffect(() => {
    // Some backends (such as AWS profile-based providers) have no interactive connection card.
    if (api && connected) api.models().then(setModels).catch(() => setModels([]));
  }, [api, connected]);

  const refreshGeneration = useRef(0);
  const refresh = useCallback(async () => {
    if (!api) return;
    const generation = ++refreshGeneration.current;
    try {
      const s = await api.status();
      if (generation !== refreshGeneration.current) return;
      setStatus(current => ({ ...s, agents: s.agents.map(a => ({
        ...a, modelCall: latestCall(current?.agents.find(prior => prior.id === a.id)?.modelCall, a.modelCall),
      })) }));
      setError(null);
      setSelected(cur => cur ?? s.agents[s.agents.length - 1]?.id ?? 'new:project');
    } catch (e) {
      if (generation !== refreshGeneration.current) return;
      setError((e as Error).message === 'unauthorized' ? 'unauthorized' : (e as Error).message);
    }
  }, [api]);

  const loadTranscript = useCallback(async (id: string) => {
    if (!api || !isAgentId(id)) return;
    const request = ++transcriptRequest.current;
    setTailError(null);
    try {
      const page = await api.transcript(id);
      if (selectedRef.current !== id || request !== transcriptRequest.current) return;
      setTail(t => {
        // The socket can deliver entries newer than this HTTP snapshot while it is in flight.
        const end = page.entries.at(-1)?.seq ?? 0;
        const entries = mergeEntries(page.entries, t?.id === id ? t.entries.filter(e => e.seq > end) : []);
        return { id, entries: entries.slice(-transcriptPageSize), hasOlder: page.hasOlder || entries.length > transcriptPageSize };
      });
    } catch (e) {
      if (selectedRef.current === id && request === transcriptRequest.current) setTailError({ id, message: (e as Error).message });
    }
  }, [api]);

  useEffect(() => { refresh(); }, [refresh]);
  useEffect(() => { if (selected) loadTranscript(selected); }, [selected, loadTranscript]);

  useEffect(() => {
    if (!api) return;
    const onEvent = (e: ServerEvent) => {
      switch (e.type) {
        case 'entry':
          if (selectedRef.current === e.agentId) setTail(t => {
            const prior = t?.id === e.agentId ? t : null;
            const entries = mergeEntries(prior?.entries ?? [], [e.entry]);
            return { id: e.agentId, entries: entries.slice(-transcriptPageSize), hasOlder: !!prior?.hasOlder || entries.length > transcriptPageSize };
          });
          if (e.entry.kind === 'assistant' || e.entry.kind === 'tool_call' || e.entry.kind === 'idle' || e.entry.kind === 'error' || e.entry.kind === 'context_reset')
            setStreaming(s => ({ ...s, [e.agentId]: '' }));
          if (e.entry.kind === 'tool_result') setRunning(r => { const { [e.agentId]: _, ...rest } = r; return rest; });
          break;
        case 'model_call_start':
        case 'model_call_end':
          setStatus(s => s && { ...s, agents: s.agents.map(a => a.id === e.agentId ? { ...a, modelCall: latestCall(a.modelCall, e.modelCall) } : a) });
          if (e.type === 'model_call_start' || e.modelCall.outcome !== 'succeeded')
            setStreaming(s => ({ ...s, [e.agentId]: '' }));
          break;
        case 'delta':
          setStreaming(s => ({ ...s, [e.agentId]: (s[e.agentId] ?? '') + e.text }));
          break;
        case 'tasks':
        case 'schedules':
        case 'processes':
        case 'tools':
        case 'agent_message':
        case 'memory':
          setTick(t => t + 1);
          break;
        case 'tool_start':
          setRunning(r => ({ ...r, [e.agentId]: e.callId }));
          break;
        case 'status':
          setStatus(s => s && { ...s, agents: s.agents.map(a => a.id === e.agentId ? { ...a, status: e.status, statusDetail: e.detail, retryAt: e.retryAt } : a) });
          break;
        case 'inbox':
          setStatus(s => s && { ...s, agents: s.agents.map(a => a.id === e.agentId ? { ...a, pending: e.pending } : a) });
          break;
        case 'flag':
        case 'agents':
        case 'config':
        case 'projects':
          refresh();
          break;
        case 'sera_message':
          setTick(t => t + 1);
          refresh();
          break;
      }
    };
    return api.events(onEvent, () => {
      // A reconnect may be to a fresh process; its attempt ids start over.
      setStatus(s => s && { ...s, agents: s.agents.map(a => ({ ...a, modelCall: null })) });
      setStreaming({});
      refresh();
      if (selectedRef.current) loadTranscript(selectedRef.current);
    });
  }, [api, refresh, loadTranscript]);

  if (!token || error === 'unauthorized')
    return <TokenGate expired={!!token} />;
  if (!api || !status)
    return <div className="gate"><p>{error ?? 'Connecting…'}</p></div>;

  const agent = status.agents.find(a => a.id === selected) ?? null;
  const project = agent ? status.projects.find(p => p.id === agent.projectId) : undefined;
  let main;
  if (agent)
    main = <><AgentView key={agent.id} api={api} agent={agent} project={project} agents={status.agents} models={models} tick={tick} defaultModel={status.config.model ?? status.config.defaultModel ?? models[0]} defaultEffort={status.config.effort} onChange={refresh} onSelect={setSelected} entries={tail?.id === agent.id ? tail.entries : undefined} hasOlder={tail?.id === agent.id && tail.hasOlder} streaming={streaming[agent.id] ?? ''} runningCall={running[agent.id]} />{tailError?.id === agent.id && <div role="alert" className="banner">Couldn’t load the transcript: {tailError.message} <button onClick={() => loadTranscript(agent.id)}>Retry</button></div>}</>;
  else if (selected === 'processes')
    main = <ProcessesView api={api} tick={tick} onSelect={setSelected} />;
  else if (selected === 'inbox')
    main = <InboxView api={api} status={status} tick={tick + status.inbox * 1000 + status.openQuestions} onSelect={setSelected} />;
  else if (selected === 'messages')
    main = <MessagesView api={api} status={status} tick={tick} onSelect={setSelected} />;
  else if (selected?.startsWith('memory:'))
    main = <MemoryView api={api} project={status.projects.find(p => p.id === selected.slice(7))} agents={status.agents} tick={tick} />;
  else if (selected === 'new:project')
    main = <NewProject api={api} onCreated={id => { refresh(); setSelected(id); }} />;
  else
    main = <div className="empty">Start a chat or a project to begin.</div>;

  const select = (id: string) => { setSelected(id); setNavOpen(false); };
  const title = agent?.name ?? (selected === 'inbox' ? 'Inbox' : selected === 'messages' ? 'Messages' : selected === 'processes' ? 'Processes'
    : selected?.startsWith('memory:') ? 'Memory' : selected === 'new:project' ? 'New project' : 'Marten');
  return (
    <div className={`shell ${navOpen ? 'nav-open' : ''}`}>
      <div className="mobilebar">
        <button className="small" onClick={() => setNavOpen(v => !v)} aria-label="Agents and settings">☰</button>
        <span className="mobile-title">{title}</span>
        {status.openQuestions > 0 && <button className="badge warn" onClick={() => select('inbox')} title="Questions waiting for you">? {status.openQuestions}</button>}
      </div>
      <Sidebar api={api} status={status} selected={selected} onSelect={select} onChange={refresh} />
      <main className="main">
        {main}
      </main>
    </div>
  );
}

/** No token yet (or a stale one): the printed link carries it, or paste it, e.g. on a phone's installed app. */
function TokenGate({ expired }: { expired: boolean }) {
  const [value, setValue] = useState('');
  const save = () => {
    const t = value.trim().replace(/^.*#token=/, '');
    if (!t) return;
    try { localStorage.setItem('marten.token', t); } catch { /* private window */ }
    location.reload();
  };
  return (
    <div className="gate">
      <h1>Marten</h1>
      <p>{expired ? 'That token no longer works. ' : ''}Open the link Marten printed when it started; it carries the token this page needs. Or paste the link or token here:</p>
      <form className="row" onSubmit={e => { e.preventDefault(); save(); }}>
        <input type="password" value={value} onChange={e => setValue(e.target.value)} placeholder="http://…/#token=… or the token" autoComplete="off" />
        <button type="submit" className="primary" disabled={!value.trim()}>Connect</button>
      </form>
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

  const [showClosed, setShowClosed] = useState(false);
  const newChat = async () => {
    const name = prompt('Name this chat', 'Chat');
    if (name === null) return;
    const a = await api.createChat(name);
    onChange();
    onSelect(a.id);
  };

  return (
    <aside className="sidebar">
      <header className="brand">
        <span>Marten</span>
        <button className={`link small ${status.paused ? 'on' : ''}`} title="Pause or resume every agent, nudge and schedule"
          onClick={() => api.pauseAll(!status.paused).then(onChange)}>{status.paused ? '▶ Resume all' : '⏸ Pause all'}</button>
      </header>
      {status.paused && <div className="banner small">Everything is paused. Agents finish their current step and hold; nudges and schedules stop.</div>}
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
        {status.backends.map(b => <BackendRow key={b.provider} api={api} backend={b} onChange={onChange} />)}
      </section>
      <nav className="agents">
        <button className={`agent inbox ${selected === 'inbox' ? 'active' : ''}`} onClick={() => onSelect('inbox')}>
          <span className="name">Inbox</span>
          {status.openQuestions > 0
            ? <span className="badge warn" title="Questions waiting for you">? {status.openQuestions}</span>
            : status.inbox > 0 && <span className="badge">{status.inbox}</span>}
        </button>
        <button className={`agent inbox ${selected === 'messages' ? 'active' : ''}`} onClick={() => onSelect('messages')}>
          <span className="name">Messages</span>
        </button>
        <button className={`agent inbox ${selected === 'processes' ? 'active' : ''}`} onClick={() => onSelect('processes')}>
          <span className="name">Processes</span>
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
                <span className="project-name" title={p.bindings.join('\n')}>{p.name}{p.paused && <span className="tag">paused</span>}</span>
                <button className="link small" title={p.paused ? 'Resume this project' : 'Pause every agent in this project'}
                  onClick={() => api.pauseProject(p.id, !p.paused).then(onChange)}>{p.paused ? 'Resume' : 'Pause'}</button>
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
        {status.agents.filter(a => a.kind === 'chat' && (!a.closed || showClosed || a.id === selected)).map(a => <AgentRow key={a.id} agent={a} selected={selected} onSelect={onSelect} />)}
        {status.agents.some(a => a.kind === 'chat' && a.closed) && (
          <button className="link small" onClick={() => setShowClosed(v => !v)}>
            {showClosed ? 'Hide closed chats' : `Show ${status.agents.filter(a => a.kind === 'chat' && a.closed).length} closed`}
          </button>
        )}
      </nav>
    </aside>
  );
}

/** An API-key backend: shows whether it's connected and takes or removes a key. The key never comes back from the server. */
function BackendRow({ api, backend: b, onChange }: { api: Api; backend: Backend; onChange: () => void }) {
  const [editing, setEditing] = useState(false);
  const [key, setKey] = useState('');
  const save = async (value: string | null) => {
    await api.setBackendKey(b.provider, value);
    setEditing(false);
    setKey('');
    onChange();
  };
  return (
    <div className="backend small">
      <div className="row">
        <span className={b.connected ? '' : 'muted'}>{b.label} · {b.connected ? (b.keySource === 'saved' ? 'key saved' : `from ${b.keySource}`) : 'no key'}</span>
        {(b.spentToday > 0 || b.budget != null) && (
          <span className={`spend ${b.budget != null && b.spentToday >= b.budget ? 'error' : 'muted'}`} title="Spent today at known prices / daily budget">
            {'$' + b.spentToday.toFixed(2)}{b.budget != null && ' / $' + b.budget}
          </span>
        )}
        {!editing && <button className="link small" onClick={() => setEditing(true)}>{b.connected ? 'Change' : 'Add key'}</button>}
        {!editing && b.keySource === 'saved' && <button className="link small" onClick={() => save(null)}>Remove</button>}
      </div>
      {editing && (
        <form className="row" onSubmit={e => { e.preventDefault(); if (key.trim()) save(key.trim()); }}>
          <input type="password" autoFocus placeholder={`${b.label} API key`} value={key} onChange={e => setKey(e.target.value)} autoComplete="off" />
          <button type="submit" className="small" disabled={!key.trim()}>Save</button>
          <button type="button" className="link small" onClick={() => { setEditing(false); setKey(''); }}>Cancel</button>
        </form>
      )}
    </div>
  );
}

function AgentRow({ agent: a, selected, onSelect, nested }: { agent: AgentSummary; selected: string | null; onSelect: (id: string) => void; nested?: boolean }) {
  return (
    <button className={`agent ${a.id === selected ? 'active' : ''} ${nested ? 'nested' : ''} ${a.closed ? 'closed' : ''}`} onClick={() => onSelect(a.id)}>
      <span className={`dot ${a.pausedEffective && a.status !== 'running' ? 'paused' : a.status}`}
        title={a.closed ? 'Closed' : a.pausedEffective ? `Paused · ${statusLabel[a.status]}` : statusLabel[a.status]} />
      <span className="name">{a.name}</span>
      {a.openTasks > 0 && <span className="badge" title="Open tasks">{a.openTasks}</span>}
      {a.pending > 0 && <span className="badge" title="Queued messages">✉ {a.pending}</span>}
      {(a.abandonedRunspaces.length > 0 || a.nudgesStopped) && <span className="badge warn" title={a.nudgesStopped ? 'Nudges stopped' : 'Abandoned runspaces'}>!</span>}
    </button>
  );
}

const emptyTranscript: Entry[] = [];

type ArchivePage = {
  entries: Entry[];
  hasOlder: boolean;
  // Only the current page's entries are retained. Each cursor can refetch its page.
  cursors: number[];
};

function AgentView({ api, agent, project, agents, models, tick, defaultModel, defaultEffort, onChange, onSelect, entries: transcript, hasOlder, streaming, runningCall }: {
  api: Api; agent: AgentSummary; project?: Project; agents: AgentSummary[]; models: string[]; tick: number;
  defaultModel?: string; defaultEffort?: string; onChange: () => void; onSelect: (id: string) => void;
  entries?: Entry[]; hasOlder: boolean; streaming: string; runningCall?: string;
}) {
  const [archive, setArchive] = useState<ArchivePage | null>(null);
  const [loadingPage, setLoadingPage] = useState(false);
  const [pageError, setPageError] = useState<string | null>(null);
  const pageRequest = useRef(0);
  const entries = archive?.entries ?? transcript ?? emptyTranscript;
  const canGoOlder = entries.length > 0 && (archive?.hasOlder ?? hasOlder);
  const loadPage = async (cursors: number[]) => {
    const request = ++pageRequest.current;
    setPageError(null);
    setLoadingPage(true);
    try {
      const page = await api.transcript(agent.id, cursors.at(-1));
      if (request === pageRequest.current) setArchive({ ...page, cursors });
    } catch (e) {
      if (request === pageRequest.current) setPageError(`Could not load transcript page: ${e instanceof Error ? e.message : String(e)}`);
    } finally {
      if (request === pageRequest.current) setLoadingPage(false);
    }
  };
  const [tasks, setTasks] = useState<TaskItem[]>([]);
  useEffect(() => { api.tasks(agent.id).then(setTasks).catch(() => setTasks([])); }, [api, agent.id, tick]);
  const [questions, setQuestions] = useState<SeraMessage[]>([]);
  useEffect(() => {
    let active = true;
    api.inbox().then(all => {
      if (!active) return;
      setQuestions(current => {
        const answered = new Map(current.filter(q => q.answer).map(q => [q.id, q]));
        return all.filter(m => m.question && m.from === agent.id).map(q => answered.get(q.id) ?? q);
      });
    }).catch(() => {});
    return () => { active = false; };
  }, [api, agent.id, tick]);
  const updateQuestion = (answer: SeraMessage) => setQuestions(current => current.map(q => q.id === answer.id ? answer : q));
  const [procs, setProcs] = useState<BackgroundProcess[]>([]);
  useEffect(() => { api.agentProcesses(agent.id).then(setProcs).catch(() => setProcs([])); }, [api, agent.id, tick]);
  const [schedules, setSchedules] = useState<Schedule[]>([]);
  useEffect(() => { api.schedules(agent.id).then(setSchedules).catch(() => setSchedules([])); }, [api, agent.id, tick]);
  const parent = agents.find(a => a.id === agent.parentId);
  const [text, setText] = useState('');
  const [mode, setMode] = useState<'queued' | 'immediate'>('queued');
  const [showNotebook, setShowNotebook] = useState(false);
  const [resetting, setResetting] = useState(false);
  const [resetError, setResetError] = useState<string | null>(null);
  const scroller = useRef<HTMLDivElement>(null);
  const busy = agent.status === 'running' || agent.status === 'waiting_for_quota' || agent.status === 'waiting_for_network' || agent.status === 'waiting_for_slot';

  const openedTranscript = useRef<string>();
  const followLatest = useRef(true);
  const returnToLatest = () => {
    ++pageRequest.current; // Ignore an archive fetch still in flight.
    setLoadingPage(false);
    setPageError(null);
    followLatest.current = true;
    setArchive(null);
  };
  useLayoutEffect(() => {
    if (archive && scroller.current) scroller.current.scrollTop = 0;
  }, [archive]);
  useLayoutEffect(() => {
    const el = scroller.current;
    if (!transcript) openedTranscript.current = undefined;
    if (!el || !transcript || showNotebook || archive) return;
    // On a cold open this is the first commit with history, not the empty view before fetch.
    if (openedTranscript.current !== agent.id) {
      openedTranscript.current = agent.id;
      followLatest.current = true;
    }
    if (followLatest.current) el.scrollTop = el.scrollHeight;
  }, [agent.id, transcript, streaming, runningCall, showNotebook, questions, archive]);

  const trackLogScroll = (event: React.UIEvent<HTMLDivElement>) => {
    const el = event.currentTarget;
    if (!showNotebook && !archive && openedTranscript.current === agent.id)
      followLatest.current = el.scrollHeight - el.scrollTop - el.clientHeight < 200;
  };

  const imageUrl = useCallback((path: string) => api.imageUrl(agent.id, path), [api, agent.id]);
  const results = useMemo(() => new Map(entries.filter(e => e.kind === 'tool_result').map(e => [e.callId, e])), [entries]);
  const agentQuestions = questions.filter(q => q.from === agent.id);
  const delivered = new Set(entries.filter(e => e.kind === 'message' && e.from === 'Sera').map(e => e.text));
  // Keep showing the recorded answer until its ordinary inbound message appears. Approval
  // answers are handled by Guard and may never become ordinary inbound messages.
  const answeredCards = agentQuestions.filter(q => q.answer && (!hasOlder || !entries.length || Date.parse(q.answeredAt ?? q.at) >= Date.parse(entries[0].ts)) && (!archive || Date.parse(q.answeredAt ?? q.at) <= Date.parse(entries.at(-1)!.ts)) && !delivered.has(`Sera answered your question "${q.text}":\n\n${q.answer}`))
    .sort((a, b) => Date.parse(a.answeredAt ?? a.at) - Date.parse(b.answeredAt ?? b.at));
  const card = (q: SeraMessage) => <div key={`question-${q.id}`} className="inbox-item card-inline"><QuestionCard api={api} q={q} onAnswered={updateQuestion} /></div>;
  let nextCard = 0;

  const callIds = useMemo(() => new Set(entries.filter(e => e.kind === 'tool_call' && e.callId).map(e => e.callId)), [entries]);

  const startFreshContext = async () => {
    if (!window.confirm(`Start fresh context for ${agent.name}? The agent will no longer see earlier messages, tool results, or compaction summaries. The transcript stays browsable, and current instructions, memory, and queued input are kept.`)) return;
    setResetting(true);
    setResetError(null);
    try {
      await api.startFreshContext(agent.id);
    } catch (e) {
      setResetError(e instanceof Error ? e.message : String(e));
    } finally {
      setResetting(false);
      onChange();
    }
  };

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
            {agent.pausedEffective && <span className="tag">paused</span>}
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
          <div className="head-actions">
            <button className="small" disabled={resetting || busy || agent.pausedEffective || agent.status === 'paused'}
              title="Start a new model context without deleting the transcript" onClick={startFreshContext}>Start fresh context</button>
            <button className={`small ${showNotebook ? 'on' : ''}`} onClick={() => setShowNotebook(v => !v)}
              title="Every PowerShell run with its $State in and out; re-run or export as a Jupyter notebook">{showNotebook ? 'Transcript' : 'Notebook'}</button>
            <button className="small" onClick={() => api.closeAgent(agent.id, !agent.closed).then(onChange)}
              title={agent.closed ? 'Reopen this agent' : 'Close this agent: it keeps its history, stops its processes and schedules, and wakes again when messaged'}>{agent.closed ? 'Reopen' : 'Close'}</button>
            <button className="small" onClick={() => api.pauseAgent(agent.id, !agent.paused).then(onChange)}
              title={agent.paused ? 'Resume this agent' : 'Hold this agent before its next model call'}>{agent.paused ? '▶ Resume' : '⏸ Pause'}</button>
          </div>
          <div className={`state ${agent.status}`}>{statusLabel[agent.status]}{agent.statusDetail ? ` · ${agent.statusDetail}` : ''}{agent.retryAt && ` · Next retry at ${new Date(agent.retryAt).toLocaleTimeString()}`}</div>
          {agent.modelCall && <div className="small muted">
            {agent.modelCall.kind === 'compaction' ? 'Context compaction' : 'Model reply'} · {agent.modelCall.model} · started {new Date(agent.modelCall.startedAt).toLocaleTimeString()}
            {agent.modelCall.endedAt ? ` · ${agent.modelCall.outcome} at ${new Date(agent.modelCall.endedAt).toLocaleTimeString()}` : ' · in flight'}
          </div>}
        </div>
      </header>
      {resetError && <div className="banner" role="alert">Could not start fresh context: {resetError}</div>}
      {tasks.length > 0 && (
        <div className="tasks task-list" role="region" aria-label="Agent tasks" tabIndex={0}>
          {tasks.map(t => (
            <span key={t.id} className={`task ${t.state}`} title={[t.reason, t.check && `Done when \`${t.check}\` passes`, t.lastCheck && `Last check ${formatWhen(t.lastCheck.at)}: ${t.lastCheck.passed ? 'passed' : 'failed'}\n${t.lastCheck.output.slice(-1500)}`].filter(Boolean).join('\n\n')}>
              <span className="task-state">{t.state}</span> #{t.id} {t.title}
              {t.waitingOn ? <> · waiting for <button className="link small" onClick={() => onSelect(t.waitingOn!.agentId)}>{agents.find(a => a.id === t.waitingOn!.agentId)?.name ?? t.waitingOn.agentId} #{t.waitingOn.taskId}</button></> : t.reason && t.state !== 'open' ? ` · ${t.reason}` : ''}
              {t.check && <span className={`check ${t.lastCheck ? (t.lastCheck.passed ? 'pass' : 'fail') : ''}`}>{t.lastCheck ? (t.lastCheck.passed ? '✓' : '✗') : '◇'} check</span>}
            </span>
          ))}
        </div>
      )}
      {procs.length > 0 && (
        <div className="tasks processes">
          {procs.map(p => (
            <span key={p.id} className={`task proc ${p.state}`} title={`${p.command}\n${p.workingDirectory}\nLog: ${p.logPath}`}>
              <span className="task-state">{p.state}</span> #{p.id} {p.name} <span className="muted">pid {p.pid}</span>
              <button className="link small" onClick={() => onSelect('processes')}>log</button>
              <button className="link small" title="Stop it and everything it spawned" onClick={() => api.stopProcess(agent.id, p.id)}>stop</button>
            </span>
          ))}
        </div>
      )}
      {schedules.length > 0 && (
        <div className="tasks schedules">
          {schedules.map(s => (
            <span key={s.id} className="task schedule" title={`${s.command}${s.lastResult ? `\n\nLast run: ${s.lastResult}` : ''}`}>
              <span className="task-state">{s.everySeconds ? `every ${formatEvery(s.everySeconds)}` : 'once'}</span> #{s.id} {s.description ?? s.command.split('\n')[0].slice(0, 60)}
              <span className="muted"> · next {formatWhen(s.nextRun)}</span>
              <button className="link small" title="Remove this schedule" onClick={() => api.removeSchedule(agent.id, s.id)}>×</button>
            </span>
          ))}
        </div>
      )}
      {agent.pausedEffective && !agent.paused && (
        <div className="banner">Paused {project?.paused ? 'with its project' : 'with everything'}. Messages queue until it resumes.</div>
      )}
      {agent.nudgesStopped && (
        <div className="banner">Nudges stopped: this agent went idle three times with open tasks and nothing changed. Message it to get it going again.</div>
      )}
      {agent.abandonedRunspaces.length > 0 && (
        <div className="banner">
          {agent.abandonedRunspaces.length} PowerShell runspace{agent.abandonedRunspaces.length > 1 ? 's' : ''} abandoned after a timeout. Last: <code>{agent.abandonedRunspaces.at(-1)!.command.slice(0, 120)}</code>. Restart Marten to reclaim them.
        </div>
      )}
      {showNotebook && <NotebookView api={api} agent={agent} tick={tick} />}
      {!showNotebook && (canGoOlder || archive || loadingPage || pageError) && (
        <nav className="tasks" aria-label="Transcript pages">
          <button className="small" disabled={!canGoOlder || loadingPage} onClick={() => loadPage([...(archive?.cursors ?? []), entries[0].seq])}>Older</button>
          {archive && <>
            <button className="small" disabled={loadingPage} onClick={() => archive.cursors.length > 1 ? loadPage(archive.cursors.slice(0, -1)) : returnToLatest()}>Newer</button>
            <button className="small" onClick={returnToLatest}>Return to latest</button>
          </>}
          <span className="muted small" role="status">{loadingPage ? 'Loading page…' : archive ? 'Archived page' : 'Latest entries'}</span>
          {pageError && <span className="error small" role="alert">{pageError}</span>}
        </nav>
      )}
      <div className="log" ref={scroller} onScroll={trackLogScroll} style={showNotebook ? { display: 'none' } : undefined}>
        {!archive && !transcript && <div className="muted small" role="status">Loading transcript…</div>}
        {entries.flatMap(e => {
          const before = [];
          while (nextCard < answeredCards.length && Date.parse(answeredCards[nextCard].answeredAt ?? answeredCards[nextCard].at) <= Date.parse(e.ts))
            before.push(card(answeredCards[nextCard++]));
          if (e.kind === 'tool_result' && callIds.has(e.callId)) return before;
          return [...before, <EntryView key={e.seq} entry={e} result={e.kind === 'tool_call' ? results.get(e.callId) : undefined} running={!archive && !!runningCall && e.callId === runningCall} imageUrl={imageUrl} />];
        })}
        {answeredCards.slice(nextCard).map(card)}
        {!archive && streaming && <div className="assistant streaming"><Md text={streaming} /></div>}
        {!archive && agent.modelCall && !agent.modelCall.endedAt && !streaming && !runningCall &&
          <Thinking label={agent.modelCall.kind === 'compaction' ? 'Compacting context' : 'Thinking'} />}
        {!archive && agent.status === 'waiting_for_quota' && <Thinking label="Waiting for quota" />}
        {!archive && agent.status === 'waiting_for_network' && <Thinking label="Waiting for network" />}
        {!archive && agent.status === 'waiting_for_slot' && <Thinking label="Waiting for slot" />}
        {!archive && agentQuestions.filter(q => !q.answer).map(card)}
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

/** The agent's PowerShell runs as cells: what went in ($State, directory, command), what came out, and a re-run to check it reproduces. */
function NotebookView({ api, agent, tick }: { api: Api; agent: AgentSummary; tick: number }) {
  const [cells, setCells] = useState<NotebookCell[]>([]);
  const [reruns, setReruns] = useState<Record<number, { run: NotebookCell; sameOutput: boolean; sameState: boolean } | 'running' | string>>({});
  useEffect(() => { api.notebook(agent.id).then(setCells).catch(() => setCells([])); }, [api, agent.id, tick]);
  const rerun = async (n: number) => {
    setReruns(r => ({ ...r, [n]: 'running' }));
    try { const res = await api.rerunCell(agent.id, n); setReruns(r => ({ ...r, [n]: res })); }
    catch (e) { setReruns(r => ({ ...r, [n]: String((e as Error).message ?? e) })); }
  };
  const exportIpynb = async () => {
    const blob = await api.notebookFile(agent.id);
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = `${agent.name}-notebook.ipynb`;
    a.click();
    URL.revokeObjectURL(a.href);
  };
  const json = (v: unknown) => v === undefined || v === null ? 'null' : JSON.stringify(v, null, 2);
  return (
    <div className="log notebook">
      <div className="row">
        <span className="muted small">{cells.length} PowerShell run{cells.length === 1 ? '' : 's'}. Each ran in a fresh runspace, so its directory, $State and command are everything it depended on inside Marten.</span>
        {cells.length > 0 && <button className="small" onClick={exportIpynb}>Export .ipynb</button>}
      </div>
      {cells.length === 0 && <div className="muted">No PowerShell runs yet.</div>}
      {cells.slice().reverse().map(c => {
        const r = reruns[c.id];
        return (
          <div key={c.id} className={`cell ${c.isError ? 'error' : ''}`}>
            <div className="row small">
              <strong>[{c.id}]</strong>
              <span className="muted">{formatWhen(c.at)} · {c.durationMs} ms · <code>{c.workingDirectory}</code></span>
              <button className="small" disabled={r === 'running'} onClick={() => rerun(c.id)}
                title="Run this command again with the same directory and $State going in. It really runs: files it writes get written again. The agent's own $State is untouched.">
                {r === 'running' ? 'Running…' : 'Re-run'}</button>
            </div>
            <pre className="output">{c.command}</pre>
            <details><summary className="small muted">Output{c.isError ? ' (error)' : ''}</summary><pre className="output log-tail">{c.output}</pre></details>
            <details><summary className="small muted">$State in / out</summary>
              <div className="state-diff"><pre className="output">{json(c.stateIn)}</pre><pre className="output">{json(c.stateOut)}</pre></div>
            </details>
            {r && r !== 'running' && (typeof r === 'string'
              ? <div className="error small">{r}</div>
              : <div className="small">
                  Re-run: <span className={r.sameOutput ? 'ok' : 'warn'}>{r.sameOutput ? 'same output' : 'different output'}</span>,{' '}
                  <span className={r.sameState ? 'ok' : 'warn'}>{r.sameState ? 'same $State' : 'different $State'}</span> ({r.run.durationMs} ms)
                  {!r.sameOutput && <details><summary className="muted">New output</summary><pre className="output log-tail">{r.run.output}</pre></details>}
                </div>)}
          </div>
        );
      })}
    </div>
  );
}

function formatEvery(secs: number) {
  if (secs % 86400 === 0) return `${secs / 86400}d`;
  if (secs % 3600 === 0) return `${secs / 3600}h`;
  if (secs % 60 === 0) return `${secs / 60}m`;
  return `${secs}s`;
}

function formatWhen(iso: string) {
  const d = new Date(iso);
  const sameDay = d.toDateString() === new Date().toDateString();
  return sameDay ? d.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' }) : d.toLocaleString([], { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
}

/** Hover text for a reply: the model, and the call's tokens when the backend reported them. */
function usageTitle(e: Entry) {
  const u = e.usage;
  const parts = [e.model];
  if (u?.input_tokens != null) parts.push(`${u.input_tokens.toLocaleString()} in${u.cache_read_input_tokens ? ` (${u.cache_read_input_tokens.toLocaleString()} cached)` : ''}`);
  if (u?.output_tokens != null) parts.push(`${u.output_tokens.toLocaleString()} out`);
  return parts.filter(Boolean).join(' · ') || undefined;
}

const EntryView = memo(function EntryView({ entry: e, result, running, imageUrl }: { entry: Entry; result?: Entry; running: boolean; imageUrl: (path: string) => string }) {
  switch (e.kind) {
    case 'message':
      return e.from === 'Sera'
        ? <div className="message mine">{e.text}</div>
        : e.from === 'Marten'
          ? <div className="message system"><Md text={e.text ?? ''} /></div>
          : <div className="message other"><div className="from">{shortFrom(e.from)}</div><Md text={e.text ?? ''} /></div>;
    case 'assistant':
      return e.text ? <div className="assistant" title={usageTitle(e)}><Md text={e.text} /></div> : null;
    case 'reasoning':
      return e.text ? <details className="reasoning"><summary>Reasoning</summary><div><Md text={e.text} /></div></details> : null;
    case 'tool_call':
      return <ToolCall call={e} result={result} running={running} imageUrl={imageUrl} />;
    case 'tool_result':
      return <details className={`tool ${e.isError ? 'failed' : 'done'}`}>
        <summary>Tool result{e.isError ? ' (error)' : ''} · call on another page</summary>
        <ToolOutput result={e} imageUrl={imageUrl} />
      </details>;
    case 'error':
      return <div className="error-entry">{e.text}</div>;
    case 'idle':
      return e.reason === 'interrupted' ? <div className="divider">Interrupted</div> : null;
    case 'wake':
      return e.reason === 'restart' ? <div className="divider">Resumed after restart</div>
        : e.reason === 'nudge' ? <div className="divider">Nudged: open tasks</div> : null;
    case 'elide':
      return <div className="divider">Elided {e.seqs?.length ?? 0} old tool result{e.seqs?.length === 1 ? '' : 's'} from context</div>;
    case 'context_reset':
      return <div className="divider">Fresh context started</div>;
    case 'reload':
      return <div className="divider">System prompt reloaded</div>;
    case 'compaction':
      return (
        <details className="compaction">
          <summary className="divider">{e.reason === 'whole' ? 'Context compacted' : 'Older context rolled over'} · summary</summary>
          <div><Md text={e.text ?? ''} /></div>
        </details>
      );
    default:
      return null;
  }
});

function ToolCall({ call, result, running, imageUrl }: { call: Entry; result?: Entry; running: boolean; imageUrl: (path: string) => string }) {
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
      {result && <ToolOutput result={result} imageUrl={imageUrl} />}
    </details>
  );
}

function ToolOutput({ result, imageUrl }: { result: Entry; imageUrl: (path: string) => string }) {
  return <>
    <pre className="output">{result.text}</pre>
    {result.images?.map(img => <a key={img.path} href={imageUrl(img.path)} target="_blank" rel="noreferrer"><img className="tool-image" src={imageUrl(img.path)} alt={img.path} /></a>)}
  </>;
}

/** Markdown without raw HTML: react-markdown escapes HTML unless a plugin allows it, so model output can't inject markup. */
const Md = memo(function Md({ text }: { text: string }) {
  return (
    <div className="md">
      <Markdown remarkPlugins={[remarkGfm]} components={{ a: ({ node: _, ...props }) => <a {...props} target="_blank" rel="noreferrer" /> }}>
        {text}
      </Markdown>
    </div>
  );
});

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
  const [tools, setTools] = useState<ScriptToolInfo[]>([]);
  useEffect(() => { if (project) api.scriptTools(project.id).then(setTools).catch(() => setTools([])); }, [api, project, tick]);
  if (!project) return <div className="empty">No such project.</div>;
  const name = (id: string) => agents.find(a => a.id === id)?.name ?? id;
  return (
    <div className="page">
      <div className="doc">
        <h2>{project.name} · memory</h2>
        <div className="muted small">{memory?.path}</div>
        <div className="memory">{memory?.text.trim() ? <Md text={memory.text} /> : <span className="muted">Empty so far.</span>}</div>
        {memory?.prompt && (<><h3>Project prompt</h3><div className="memory"><Md text={memory.prompt} /></div></>)}
        <h3>Agent-written tools</h3>
        {tools.length === 0 && <div className="muted small">None yet. Agents write them with define_tool.</div>}
        {tools.map(t => (
          <details key={t.path} className="tool-def">
            <summary><strong>{t.name}</strong> <span className="tag">{t.scope}</span>{t.pendingReview && <span className="tag warn">needs review</span>} <span className="muted small">{t.error ? `broken: ${t.error}` : t.description?.split('\n')[0]}</span></summary>
            <div className="muted small">{t.path}</div>
            {t.pendingReview && <button className="small" onClick={() => api.approveTool(t.path).then(() => api.scriptTools(project.id).then(setTools))}>Approve this version</button>}
            <pre className="output log-tail">{t.script}</pre>
          </details>
        ))}
        <h3>History</h3>
        {memory && memory.log.length === 0 && <div className="muted small">No edits yet.</div>}
        <ul className="history">
          {memory?.log.slice().reverse().map((e, i) => (
            <li key={i}>
              <div className="small"><strong>{name(e.agentId)}</strong> <code>{e.path ?? 'MEMORY.md'}</code> <span className="muted">{time(e.at)}{e.reason ? ` · ${e.reason}` : ''}</span></div>
              {e.removed.map((l, j) => <div key={`r${j}`} className="diff del">− {l}</div>)}
              {e.added.map((l, j) => <div key={`a${j}`} className="diff add">+ {l}</div>)}
            </li>
          ))}
        </ul>
      </div>
    </div>
  );
}

function McpPanel({ api, tick, onSelect }: { api: Api; tick: number; onSelect: (id: string) => void }) {
  const [servers, setServers] = useState<McpServer[]>([]);
  const [busy, setBusy] = useState(false);
  useEffect(() => { api.mcp().then(setServers).catch(() => setServers([])); }, [api, tick]);
  const refresh = async () => {
    setBusy(true);
    try { await api.refreshMcp(); setServers(await api.mcp()); } finally { setBusy(false); }
  };
  return (
    <>
      <div className="row">
        <h2>MCP servers</h2>
        <button className="small" onClick={refresh} disabled={busy} title="Re-read config.json's mcp section and every server's tool list">{busy ? 'Reading…' : 'Reload'}</button>
      </div>
      {servers.length === 0 && <div className="muted small">None configured. Add them under <code>mcp</code> in ~/.marten/config.json, e.g. <code>{'{"mcp": {"fs": {"command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"]}}}'}</code>, then Reload.</div>}
      {servers.map(s => (
        <div key={s.name} className="inbox-item">
          <div><strong>{s.name}</strong> <span className="muted small"><code>{[s.command, ...s.args].join(' ')}</code>{s.disabled ? ' · disabled' : ''}</span></div>
          {s.error && <div className="error small">{s.error}</div>}
          <div className="small muted">{s.tools.length} tool{s.tools.length === 1 ? '' : 's'}: {s.tools.join(', ') || 'none read yet'}</div>
          {s.running.length > 0 && (
            <div className="small">Running for {s.running.map((r, i) => <span key={r.agentId}>{i > 0 && ', '}<button className="link small" onClick={() => onSelect(r.agentId)}>{r.agentName ?? r.agentId}</button> <span className="muted">(pid {r.pid})</span></span>)}</div>
          )}
        </div>
      ))}
    </>
  );
}

/** Agents outside Marten (Claude Code sessions and the like) with their own token. */
function ClientsPanel({ api, tick }: { api: Api; tick: number }) {
  const [clients, setClients] = useState<OutsideClient[]>([]);
  const [name, setName] = useState('');
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(() => api.clients().then(setClients).catch(() => setClients([])), [api]);
  useEffect(() => { load(); }, [load, tick]);
  const add = async () => {
    setError(null);
    try { await api.addClient(name.trim()); setName(''); load(); } catch (e) { setError(String((e as Error).message ?? e)); }
  };
  return (
    <>
      <h2>Outside agents</h2>
      <div className="muted small">Agents outside Marten talk to yours through <code>scripts/marten-ext</code> or the <code>/api/ext</code> routes. See <code>docs/outside-agents.md</code>.</div>
      {clients.map(c => (
        <div key={c.name} className="inbox-item small">
          <div className="row"><strong>{c.name}</strong>{c.unread > 0 && <span className="muted">{c.unread} unread by it</span>}
            <button className="link small" onClick={() => api.removeClient(c.name).then(load)}>Remove</button></div>
          <div className="muted">token <code>{c.tokenPath}</code></div>
        </div>
      ))}
      <form className="row" onSubmit={e => { e.preventDefault(); if (name.trim()) add(); }}>
        <input placeholder="New client name, e.g. openthedas" value={name} onChange={e => setName(e.target.value)} />
        <button type="submit" className="small" disabled={!name.trim()}>Add</button>
      </form>
      {error && <div className="error small">{error}</div>}
    </>
  );
}

/** Snapshots of ~/.marten. The passphrase never comes back from the server. */
function BackupsPanel({ api, tick }: { api: Api; tick: number }) {
  const [info, setInfo] = useState<BackupStatus | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState(false);
  const [pass, setPass] = useState('');
  useEffect(() => { api.backups().then(setInfo).catch(() => setInfo(null)); }, [api, tick]);
  const run = async (f: () => Promise<BackupStatus>) => {
    setBusy(true); setError(null);
    try { setInfo(await f()); } catch (e) { setError(String((e as Error).message ?? e)); } finally { setBusy(false); }
  };
  if (!info) return null;
  const last = info.snapshots[0];
  return (
    <>
      <div className="row">
        <h2>Backups</h2>
        <button className="small" disabled={busy} onClick={() => run(api.backupNow)}>{busy ? 'Working…' : 'Back up now'}</button>
      </div>
      <div className="small">
        {last ? <>Last snapshot {formatWhen(last.taken)}, {(last.bytes / 1024 / 1024).toFixed(1)} MB{last.encrypted ? ', encrypted' : ''}. </> : 'No snapshots yet. '}
        <span className="muted">{info.snapshots.length} kept in <code>{info.directory}</code>, {info.intervalMinutes > 0 ? `every ${info.intervalMinutes} min when something changed` : 'automatic snapshots off'}.</span>
      </div>
      <div className="row small">
        <span className={info.encrypting ? '' : 'muted'}>{info.encrypting ? 'Encrypted with your passphrase' : 'Not encrypted (secrets are always left out)'}</span>
        {!editing && <button className="link small" onClick={() => setEditing(true)}>{info.encrypting ? 'Change passphrase' : 'Encrypt'}</button>}
        {!editing && info.encrypting && <button className="link small" onClick={() => run(() => api.setBackupPassphrase(null))}>Stop encrypting</button>}
      </div>
      {editing && (
        <form className="row" onSubmit={e => { e.preventDefault(); if (pass.trim()) run(() => api.setBackupPassphrase(pass.trim())).then(() => { setEditing(false); setPass(''); }); }}>
          <input type="password" autoFocus placeholder="Passphrase (keep a copy outside Marten)" value={pass} onChange={e => setPass(e.target.value)} autoComplete="new-password" />
          <button type="submit" className="small" disabled={!pass.trim()}>Save</button>
          <button type="button" className="link small" onClick={() => { setEditing(false); setPass(''); }}>Cancel</button>
        </form>
      )}
      {error && <div className="error small">{error}</div>}
    </>
  );
}

function ProcessesView({ api, tick, onSelect }: { api: Api; tick: number; onSelect: (id: string) => void }) {
  const [procs, setProcs] = useState<BackgroundProcess[]>([]);
  const [open, setOpen] = useState<string | null>(null);
  const [log, setLog] = useState('');
  const [showStopped, setShowStopped] = useState(false);
  useEffect(() => { api.processes().then(setProcs).catch(() => setProcs([])); }, [api, tick]);
  useEffect(() => {
    if (!open) return;
    const [agentId, n] = open.split('#');
    const load = () => api.processLog(agentId, Number(n)).then(setLog);
    load();
    const timer = setInterval(load, 2000);
    return () => clearInterval(timer);
  }, [api, open]);
  const shown = procs.filter(p => showStopped || p.state !== 'stopped');
  return (
    <div className="page">
      <div className="doc wide">
        <McpPanel api={api} tick={tick} onSelect={onSelect} />
        <BackupsPanel api={api} tick={tick} />
        <ClientsPanel api={api} tick={tick} />
        <h2>Background processes</h2>
        <label className="small muted"><input type="checkbox" checked={showStopped} onChange={e => setShowStopped(e.target.checked)} /> Show stopped</label>
        {shown.length === 0 && <div className="muted">Nothing running. Agents start long-lived work like dev servers with process_start.</div>}
        {shown.map(p => {
          const key = `${p.agentId}#${p.id}`;
          return (
            <div key={key} className="inbox-item">
              <div className="row">
                <div>
                  <span className={`dot ${p.state === 'running' ? 'running' : p.state === 'exited' ? 'error' : 'idle'}`} />{' '}
                  <strong>{p.name}</strong> <span className="muted small">pid {p.pid} · {p.state} · started {time(p.started)} · by </span>
                  <button className="link small" onClick={() => onSelect(p.agentId)}>{p.agentName}</button>
                </div>
                <div>
                  <button className="link small" onClick={() => setOpen(open === key ? null : key)}>{open === key ? 'Hide log' : 'Log'}</button>
                  {p.state !== 'stopped' && <button className="link small" onClick={() => api.stopProcess(p.agentId, p.id).then(() => api.processes().then(setProcs))}>Stop</button>}
                </div>
              </div>
              <div className="small muted"><code>{p.command}</code> in {p.workingDirectory}</div>
              {open === key && <pre className="output log-tail">{log || '(empty)'}</pre>}
            </div>
          );
        })}
      </div>
    </div>
  );
}

function QuestionCard({ api, q, onAnswered }: { api: Api; q: SeraMessage; onAnswered?: (answer: SeraMessage) => void }) {
  const [text, setText] = useState('');
  const [error, setError] = useState<string | null>(null);
  const answer = (choice: number | null) =>
    api.answer(q.id, choice, text.trim() || null).then(answered => {
      setText('');
      onAnswered?.(answered);
    }).catch(e => setError(String(e.message ?? e)));
  return (
    <div className="question">
      <div className="q-text"><Md text={q.text} /></div>
      {q.context && <div className="q-context small"><Md text={q.context} /></div>}
      {q.answer ? (
        <div className="q-answer small"><span className="muted">You answered{q.answeredAt ? ` ${time(q.answeredAt)}` : ''}:</span> {q.answer}</div>
      ) : (
        <>
          {q.options && (
            <div className="q-options">
              {q.options.map((o, i) => <button key={i} className={i === 0 ? 'primary' : ''} onClick={() => answer(i)} title={text.trim() ? 'Answer with this option and your note' : ''}>{o}</button>)}
            </div>
          )}
          <div className="q-free">
            <input value={text} onChange={e => setText(e.target.value)} placeholder={q.options ? 'Add a note, or answer in your own words' : 'Your answer'}
              onKeyDown={e => { if (e.key === 'Enter' && text.trim()) answer(null); }} />
            <button onClick={() => answer(null)} disabled={!text.trim()}>Answer</button>
          </div>
          {error && <div className="error small">{error}</div>}
        </>
      )}
    </div>
  );
}

function MessagesView({ api, status, tick, onSelect }: { api: Api; status: Status_; tick: number; onSelect: (id: string) => void }) {
  const [projectId, setProjectId] = useState('');
  const [messages, setMessages] = useState<AgentMessage[]>([]);
  useEffect(() => { api.agentMessages({ projectId: projectId || undefined }).then(setMessages).catch(() => setMessages([])); }, [api, projectId, tick]);
  return (
    <div className="page">
      <div className="doc wide">
        <h2>Messages between agents</h2>
        <select value={projectId} onChange={e => setProjectId(e.target.value)}>
          <option value="">All projects and chats</option>
          {status.projects.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
        </select>
        {messages.length === 0 && <div className="muted">No messages yet.</div>}
        {messages.map((m, i) => (
          <div key={`${m.at}-${i}`} className="inbox-item">
            <div className="small">
              <button className="link" onClick={() => onSelect(m.fromId)}>{m.fromName}</button>
              <span className="muted"> → </span>
              <button className="link" onClick={() => onSelect(m.toId)}>{m.toName}</button>
              <span className="muted"> · {m.mode}{m.reason ? ` · ${m.reason}` : ''} · {time(m.at)}</span>
            </div>
            <Md text={m.text} />
          </div>
        ))}
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
              {m.question ? <QuestionCard api={api} q={m} /> : <Md text={m.text} />}
            </div>
          );
        })}
      </div>
    </div>
  );
}
