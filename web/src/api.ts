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
export interface SeraMessage { id: string; at: string; from: string; fromName: string; projectId?: string; text: string }

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
  model?: string;
}

export interface Status_ {
  auth: { connected: boolean; email?: string; signingIn: boolean; lastError?: string };
  config: { model?: string; effort?: string; defaultModel?: string };
  agents: AgentSummary[];
  projects: Project[];
  inbox: number;
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
  setConfig = (model: string | null, effort: string | null) => this.call<void>('PUT', '/config', { model, effort });
  createChat = (name: string) => this.call<AgentSummary>('POST', '/agents', { name });
  transcript = (id: string) => this.call<Entry[]>('GET', `/agents/${id}/transcript`);
  send = (id: string, text: string, mode: 'immediate' | 'queued') => this.call<void>('POST', `/agents/${id}/messages`, { text, mode });
  setAgentSettings = (id: string, model: string | null, effort: string | null) =>
    this.call<AgentSummary>('PUT', `/agents/${id}/settings`, { model, effort });
  tasks = (id: string) => this.call<TaskItem[]>('GET', `/agents/${id}/tasks`);
  schedules = (id: string) => this.call<Schedule[]>('GET', `/agents/${id}/schedules`);
  removeSchedule = (id: string, scheduleId: number) => this.call<void>('DELETE', `/agents/${id}/schedules/${scheduleId}`);
  createProject = (p: { name: string; bindings: string[]; prompt: string | null; worktreesByDefault: boolean; brief: string | null }) =>
    this.call<Project>('POST', '/projects', p);
  memory = (projectId: string) => this.call<ProjectMemory>('GET', `/projects/${projectId}/memory`);
  inbox = () => this.call<SeraMessage[]>('GET', '/inbox');
  interrupt = (id: string) => this.call<void>('POST', `/agents/${id}/interrupt`);
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
