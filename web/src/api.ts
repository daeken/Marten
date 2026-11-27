export type Kind = 'chat' | 'orchestrator' | 'task';
export type Status = 'idle' | 'running' | 'waiting_for_quota' | 'waiting_for_network' | 'waiting_for_slot' | 'paused' | 'error';

export interface ModelCallStatus {
  id: number;
  model: string;
  kind: 'reply' | 'compaction';
  startedAt: string;
  endedAt?: string | null;
  outcome?: 'succeeded' | 'failed' | 'cancelled' | null;
}

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
  retryAt?: string | null;
  modelCall?: ModelCallStatus | null;
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

export interface ScriptToolInfo { name: string; scope: 'global' | 'project'; path: string; script: string; description?: string; error?: string; modified: string; pendingReview?: boolean }

export interface OutsideClient { name: string; created: string; tokenPath: string; unread: number }

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

export interface NotebookCell {
  id: number; at: string; command: string; workingDirectory: string; stateIn?: unknown; stateOut?: unknown;
  output: string; isError: boolean; durationMs: number;
}

export interface TaskItem {
  id: number;
  title: string;
  state: 'open' | 'waiting' | 'blocked' | 'done';
  waitingOn?: { agentId: string; taskId: number } | null;
  reason?: string;
  assignedBy: string;
  updated: string;
  check?: string;
  lastCheck?: { at: string; passed: boolean; exitCode?: number; output: string };
}

export interface MemoryEdit { at: string; agentId: string; reason?: string; path?: string; added: string[]; removed: string[] }
export interface ProjectMemory { path: string; text: string; prompt: string; log: MemoryEdit[] }
export interface SeraMessage {
  id: string; at: string; from: string; fromName: string; projectId?: string; text: string;
  question?: boolean; options?: string[]; context?: string; answer?: string; answeredAt?: string;
}
export interface AgentMessage {
  at: string; fromId: string; fromName: string; toId: string; toName: string; projectId?: string;
  mode: 'immediate' | 'queued'; text: string; reason?: string;
}

export type ArtifactRef = { id: string; name: string; bytes: number; previewKind: 'none' | 'text' | 'png' };

export interface Entry {
  seq: number;
  ts: string;
  kind: 'wake' | 'message' | 'assistant' | 'reasoning' | 'tool_call' | 'tool_result' | 'error' | 'idle' | 'elide' | 'compaction' | 'reload' | 'context_reset';
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
  artifacts?: ArtifactRef[];
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
  | { type: 'status'; agentId: string; status: Status; detail?: string; retryAt?: string | null }
  | { type: 'model_call_start' | 'model_call_end'; agentId: string; modelCall: ModelCallStatus }
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

export interface Device { id: string; name: string; createdAt: string }
export interface PairStatus { enabled: boolean; requiresTailscaleEnrollment: boolean; devices: Device[] }
export interface PairInvitation { payload: string; expiresAt: string }

export const transcriptPageSize = 80;
export type TranscriptPage = { entries: Entry[]; hasOlder: boolean };

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

  startFreshContext = (id: string) => this.call<void>('POST', `/agents/${id}/start-fresh-context`);
  status = () => this.call<Status_>('GET', '/status');
  pairStatus = () => this.call<PairStatus>('GET', '/pair/devices');
  createPairCode = (tsAuthKey?: string) => this.call<PairInvitation>('POST', '/pair/create', tsAuthKey ? { tsAuthKey } : undefined);
  revokeDevice = (id: string) => this.call<void>('DELETE', `/pair/devices/${id}`);
  login = () => this.call<{ url: string }>('POST', '/auth/login');
  logout = () => this.call<void>('POST', '/auth/logout');
  models = () => this.call<string[]>('GET', '/models');
  setBackendKey = (provider: string, key: string | null) => this.call<Backend>('PUT', `/backends/${provider}/key`, { key });
  setConfig = (model: string | null, effort: string | null) => this.call<void>('PUT', '/config', { model, effort });
  createChat = (name: string) => this.call<AgentSummary>('POST', '/agents', { name });
  transcript = async (id: string, before?: number): Promise<TranscriptPage> => {
    const page = await this.call<TranscriptPage | Entry[]>('GET',
      `/agents/${id}/transcript?limit=${transcriptPageSize}${before === undefined ? '' : `&before=${before}`}`);
    if (!Array.isArray(page)) return page;
    // The static UI can update before the service restarts into a paging-capable build.
    const end = before === undefined ? page.length : page.findIndex(e => e.seq >= before);
    const stop = end < 0 ? page.length : end;
    return { entries: page.slice(Math.max(0, stop - transcriptPageSize), stop), hasOlder: stop > transcriptPageSize };
  };
  send = (id: string, text: string, mode: 'immediate' | 'queued') => this.call<void>('POST', `/agents/${id}/messages`, { text, mode });
  setAgentSettings = (id: string, model: string | null, effort: string | null) =>
    this.call<AgentSummary>('PUT', `/agents/${id}/settings`, { model, effort });
  notebook = (id: string) => this.call<NotebookCell[]>('GET', `/agents/${id}/notebook?limit=300`);
  rerunCell = (id: string, n: number) => this.call<{ run: NotebookCell; sameOutput: boolean; sameState: boolean }>('POST', `/agents/${id}/notebook/${n}/rerun`);
  notebookFile = async (id: string) => {
    const res = await fetch(`/api/agents/${id}/notebook.ipynb`, { headers: { Authorization: `Bearer ${this.token}` } });
    if (!res.ok) throw new Error(`Export failed (${res.status})`);
    return res.blob();
  };
  tasks = (id: string) => this.call<TaskItem[]>('GET', `/agents/${id}/tasks`);
  schedules = (id: string) => this.call<Schedule[]>('GET', `/agents/${id}/schedules`);
  processes = () => this.call<BackgroundProcess[]>('GET', '/processes');
  agentProcesses = (id: string) => this.call<BackgroundProcess[]>('GET', `/agents/${id}/processes?includeStopped=true`);
  stopProcess = (id: string, n: number) => this.call<BackgroundProcess>('POST', `/agents/${id}/processes/${n}/stop`);
  processLog = async (id: string, n: number, lines = 200) => {
    const res = await fetch(`/api/agents/${id}/processes/${n}/log?lines=${lines}`, { headers: { Authorization: `Bearer ${this.token}` } });
    if (!res.ok) throw new Error(`Could not load process log (${res.status}).`);
    return res.text();
  };
  scriptTools = (projectId?: string) => this.call<ScriptToolInfo[]>('GET', `/tools${projectId ? `?projectId=${encodeURIComponent(projectId)}` : ''}`);
  removeSchedule = (id: string, scheduleId: number) => this.call<void>('DELETE', `/agents/${id}/schedules/${scheduleId}`);
  createProject = (p: { name: string; bindings: string[]; prompt: string | null; worktreesByDefault: boolean; brief: string | null }) =>
    this.call<Project>('POST', '/projects', p);
  memory = (projectId: string) => this.call<ProjectMemory>('GET', `/projects/${projectId}/memory`);
  inbox = () => this.call<SeraMessage[]>('GET', '/inbox');
  artifact = async (agentId: string, ref: ArtifactRef, preview: boolean, signal: AbortSignal) => {
    if (!/^[a-f0-9]{32}$/.test(ref.id) || !/^[a-zA-Z0-9_-]+$/.test(agentId) ||
        !Number.isInteger(ref.bytes) || ref.bytes < 0 || ref.bytes > 8 * 1024 * 1024 ||
        (preview && ref.previewKind !== 'png' && ref.previewKind !== 'text')) throw new Error('Invalid artifact');
    const png = preview && ref.previewKind === 'png';
    const text = preview && ref.previewKind === 'text';
    const expectedType = png ? 'image/png' : text ? 'text/plain' : 'application/octet-stream';
    const limit = Math.min(ref.bytes, png ? 2 * 1024 * 1024 : text ? 64 * 1024 : ref.bytes);
    const res = await fetch(`/api/agents/${agentId}/artifacts/${ref.id}${preview ? '/preview' : ''}`, {
      headers: { Authorization: `Bearer ${this.token}` }, signal, redirect: 'error', cache: 'no-store', credentials: 'omit',
    });
    if (res.status !== 200 || res.headers.get('Content-Type')?.split(';', 1)[0]?.trim() !== expectedType || !res.body) {
      await res.body?.cancel();
      throw new Error(res.status === 404 ? 'Artifact unavailable' : `Artifact request failed (${res.status})`);
    }
    const reader = res.body.getReader();
    const data = new Uint8Array(limit);
    let size = 0;
    try {
      const claimed = res.headers.get('Content-Length');
      if (claimed !== null && (!/^\d+$/.test(claimed) || Number(claimed) > limit)) throw new Error('Artifact exceeds its byte limit');
      for (;;) {
        const chunk = await reader.read();
        if (chunk.done) break;
        if (chunk.value.length > limit - size) throw new Error('Artifact exceeds its byte limit');
        data.set(chunk.value, size);
        size += chunk.value.length;
      }
      if ((!text && size !== ref.bytes) || (claimed !== null && size !== Number(claimed))) throw new Error('Incomplete artifact');
    } finally {
      await reader.cancel().catch(() => {});
      reader.releaseLock();
    }
    if (text) return { text: new TextDecoder('utf-8', { fatal: true }).decode(data.subarray(0, size)), truncated: res.headers.get('X-Marten-Preview-Truncated') === '1' };
    return { blob: new Blob([data.subarray(0, size)], { type: expectedType }) };
  };
  imageUrl = (agentId: string, path: string) =>
    `/api/agents/${agentId}/images/${encodeURIComponent(path.split(/[\\/]/).pop() ?? '')}?token=${encodeURIComponent(this.token)}`;
  mcp = () => this.call<McpServer[]>('GET', '/mcp');
  approveTool = (path: string) => this.call<void>('POST', '/tools/approve', { path });
  clients = () => this.call<OutsideClient[]>('GET', '/clients');
  addClient = (name: string) => this.call<OutsideClient>('POST', '/clients', { name });
  removeClient = (name: string) => this.call<void>('DELETE', `/clients/${encodeURIComponent(name)}`);
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
