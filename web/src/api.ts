export type Kind = 'chat' | 'orchestrator' | 'task';
export type Status = 'idle' | 'running' | 'waiting_for_quota' | 'paused' | 'error';

export interface AgentSummary {
  id: string;
  kind: Kind;
  name: string;
  created: string;
  workingDirectory: string;
  model?: string;
  effort?: string;
  status: Status;
  statusDetail?: string;
  pending: number;
  abandonedRunspaces: { at: string; command: string }[];
  projectId?: string;
  parentId?: string;
  closed?: boolean;
  worktree?: string;
  nudgesStopped: boolean;
  openTasks: number;
  /** Paused by itself; pausedEffective also counts its project and pause-all. */
  paused: boolean;
  pausedEffective: boolean;
}

export interface Project {
  id: string;
  name: string;
  created: string;
  bindings: string[];
  worktreesByDefault: boolean;
  orchestratorId?: string;
  paused: boolean;
  spentToday: number;
  totalBudget?: number;
}

export interface Schedule {
  id: number;
  command: string;
  description?: string;
  everySeconds?: number;
  nextRun: string;
  lastRun?: string;
  lastResult?: string;
}

export interface BackgroundProcess {
  agentId: string;
  agentName: string;
  id: number;
  name: string;
  command: string;
  workingDirectory: string;
  pid: number;
  started: string;
  stopped?: string;
  logPath: string;
  state: 'running' | 'exited' | 'stopped';
}

export interface ScriptToolInfo { name: string; scope: 'global' | 'project'; path: string; script: string; description?: string; error?: string; modified: string }

export interface BackupStatus {
  directory: string;
  encrypting: boolean;
  intervalMinutes: number;
  snapshots: { name: string; taken: string; bytes: number; encrypted: boolean }[];
}

export interface McpServer {
  name: string; command: string; args: string[]; disabled: boolean; tools: string[]; readAt?: string; error?: string;
  running: { agentId: string; agentName?: string; pid: number }[];
}

export interface TaskItem {
  id: number;
  title: string;
  state: 'open' | 'waiting' | 'blocked' | 'done';
  reason?: string;
  assignedBy: string;
  updated: string;
}

export interface MemoryEdit { at: string; agentId: string; reason?: string; added: string[]; removed: string[] }
export interface ProjectMemory { path: string; text: string; prompt: string; log: MemoryEdit[] }
export interface SeraMessage {
  id: string; at: string; from: string; fromName: string; projectId?: string; text: string;
  question?: boolean; options?: string[]; context?: string; answer?: string; answeredAt?: string;
}
export interface AgentMessage {
  at: string; fromId: string; fromName: string; toId: string; toName: string; projectId?: string;
  mode: 'immediate' | 'queued'; text: string; reason?: string;
}

export interface Entry {
  seq: number;
  ts: string;
  kind: 'wake' | 'message' | 'assistant' | 'reasoning' | 'tool_call' | 'tool_result' | 'error' | 'idle' | 'elide' | 'compaction';
  seqs?: number[];
  upTo?: number;
  from?: string;
  text?: string;
  reason?: string;
  callId?: string;
  name?: string;
  arguments?: string;
  isError?: boolean;
  fullOutput?: string;
  images?: { path: string; mimeType: string }[];
  model?: string;
  /** Token usage of the model call this entry ended. */
  usage?: { input_tokens?: number; output_tokens?: number; cache_read_input_tokens?: number };
}

export interface Backend {
  provider: string;
  label: string;
  connected: boolean;
  /** "saved", or the environment variable the key came from. */
  keySource?: string;
  envVar?: string;
  /** Dollars spent today at known prices, and the daily cap if one is set. */
  spentToday: number;
  budget?: number;
}

export interface Status_ {
  auth: { connected: boolean; email?: string; signingIn: boolean; lastError?: string };
  backends: Backend[];
  config: { model?: string; effort?: string; defaultModel?: string };
  agents: AgentSummary[];
  projects: Project[];
  inbox: number;
  openQuestions: number;
  paused: boolean;
}

export type ServerEvent =
  | { type: 'entry'; agentId: string; entry: Entry }
  | { type: 'delta'; agentId: string; text: string }
  | { type: 'status'; agentId: string; status: Status; detail?: string }
  | { type: 'inbox'; agentId: string; pending: number }
  | { type: 'tool_start'; agentId: string; callId: string; name: string }
  | { type: 'flag'; agentId: string; flag: string; command: string }
  | { type: 'agents' }
  | { type: 'projects' }
  | { type: 'tasks'; agentId: string }
  | { type: 'schedules'; agentId: string }
  | { type: 'processes'; agentId: string }
  | { type: 'tools' }
  | { type: 'agent_message'; message: AgentMessage }
  | { type: 'memory'; projectId: string }
  | { type: 'sera_message'; message: SeraMessage }
  | { type: 'config' };

const TOKEN_KEY = 'marten.token';

/** The token arrives once in the URL fragment Marten prints, then lives in localStorage. */
export function takeToken(): string | null {
  const match = /token=([^&]+)/.exec(location.hash);
  if (match) {
    try { localStorage.setItem(TOKEN_KEY, match[1]); } catch { /* private window */ }
    history.replaceState(null, '', location.pathname);
    return match[1];
  }
  try { return localStorage.getItem(TOKEN_KEY); } catch { return null; }
}

export class Api {
  constructor(private token: string) {}

  private async call<T>(method: string, path: string, body?: unknown): Promise<T> {
    const res = await fetch(`/api${path}`, {
      method,
      headers: { Authorization: `Bearer ${this.token}`, ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
    if (res.status === 401) throw new Error('unauthorized');
    if (!res.ok) {
      const problem = await res.json().catch(() => null);
      throw new Error(problem?.detail ?? `${method} ${path} failed (${res.status})`);
    }
    const text = await res.text();
    return (text ? JSON.parse(text) : undefined) as T;
  }

  status = () => this.call<Status_>('GET', '/status');
  login = () => this.call<{ url: string }>('POST', '/auth/login');
  logout = () => this.call<void>('POST', '/auth/logout');
  models = () => this.call<string[]>('GET', '/models');
  setBackendKey = (provider: string, key: string | null) => this.call<Backend>('PUT', `/backends/${provider}/key`, { key });
  setConfig = (model: string | null, effort: string | null) => this.call<void>('PUT', '/config', { model, effort });
  createChat = (name: string) => this.call<AgentSummary>('POST', '/agents', { name });
  transcript = (id: string) => this.call<Entry[]>('GET', `/agents/${id}/transcript`);
  send = (id: string, text: string, mode: 'immediate' | 'queued') => this.call<void>('POST', `/agents/${id}/messages`, { text, mode });
  setAgentSettings = (id: string, model: string | null, effort: string | null) =>
    this.call<AgentSummary>('PUT', `/agents/${id}/settings`, { model, effort });
  tasks = (id: string) => this.call<TaskItem[]>('GET', `/agents/${id}/tasks`);
  schedules = (id: string) => this.call<Schedule[]>('GET', `/agents/${id}/schedules`);
  processes = () => this.call<BackgroundProcess[]>('GET', '/processes');
  agentProcesses = (id: string) => this.call<BackgroundProcess[]>('GET', `/agents/${id}/processes`);
  stopProcess = (id: string, n: number) => this.call<BackgroundProcess>('POST', `/agents/${id}/processes/${n}/stop`);
  processLog = async (id: string, n: number, lines = 200) => {
    const res = await fetch(`/api/agents/${id}/processes/${n}/log?lines=${lines}`, { headers: { Authorization: `Bearer ${this.token}` } });
    return res.ok ? res.text() : '';
  };
  scriptTools = (projectId?: string) => this.call<ScriptToolInfo[]>('GET', `/tools${projectId ? `?projectId=${encodeURIComponent(projectId)}` : ''}`);
  removeSchedule = (id: string, scheduleId: number) => this.call<void>('DELETE', `/agents/${id}/schedules/${scheduleId}`);
  createProject = (p: { name: string; bindings: string[]; prompt: string | null; worktreesByDefault: boolean; brief: string | null }) =>
    this.call<Project>('POST', '/projects', p);
  memory = (projectId: string) => this.call<ProjectMemory>('GET', `/projects/${projectId}/memory`);
  inbox = () => this.call<SeraMessage[]>('GET', '/inbox');
  imageUrl = (agentId: string, path: string) =>
    `/api/agents/${agentId}/images/${encodeURIComponent(path.split(/[\\/]/).pop() ?? '')}?token=${encodeURIComponent(this.token)}`;
  mcp = () => this.call<McpServer[]>('GET', '/mcp');
  backups = () => this.call<BackupStatus>('GET', '/backups');
  backupNow = () => this.call<BackupStatus>('POST', '/backups');
  setBackupPassphrase = (key: string | null) => this.call<BackupStatus>('PUT', '/backups/passphrase', { key });
  refreshMcp = () => this.call<void>('POST', '/mcp/refresh');
  answer = (id: string, choice: number | null, text: string | null) => this.call<SeraMessage>('POST', `/questions/${id}/answer`, { choice, text });
  agentMessages = (q: { agentId?: string; projectId?: string } = {}) => {
    const params = new URLSearchParams(Object.entries(q).filter(([, v]) => v) as [string, string][]);
    return this.call<AgentMessage[]>('GET', `/messages${params.size ? `?${params}` : ''}`);
  };
  interrupt = (id: string) => this.call<void>('POST', `/agents/${id}/interrupt`);
  closeAgent = (id: string, closed: boolean) => this.call<AgentSummary>('POST', `/agents/${id}/close`, { closed });
  pauseAgent = (id: string, paused: boolean) => this.call<AgentSummary>('POST', `/agents/${id}/pause`, { paused });
  pauseProject = (id: string, paused: boolean) => this.call<Project>('POST', `/projects/${id}/pause`, { paused });
  pauseAll = (paused: boolean) => this.call<void>('POST', '/pause', { paused });

  /** Subscribe to runtime events, reconnecting with backoff. Returns an unsubscribe function. */
  events(onEvent: (e: ServerEvent) => void, onConnect: () => void): () => void {
    let socket: WebSocket | null = null;
    let closed = false;
    let delay = 500;
    const connect = () => {
      const proto = location.protocol === 'https:' ? 'wss' : 'ws';
      socket = new WebSocket(`${proto}://${location.host}/api/events?token=${encodeURIComponent(this.token)}`);
      socket.onopen = () => { delay = 500; onConnect(); };
      socket.onmessage = m => onEvent(JSON.parse(m.data));
      socket.onclose = () => {
        if (closed) return;
        setTimeout(connect, delay);
        delay = Math.min(delay * 2, 10_000);
      };
    };
    connect();
    return () => { closed = true; socket?.close(); };
  }
}
