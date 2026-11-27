import { useEffect, useId, useLayoutEffect, useRef, useState } from 'react';
import { Api, BackgroundProcess } from './api';

export function ProcessPanels({ api, agentId, tick, onSelect }: {
  api: Api; agentId?: string; tick: number; onSelect: (id: string) => void;
}) {
  const [processes, setProcesses] = useState<BackgroundProcess[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [revision, setRevision] = useState(0);

  useEffect(() => {
    let disposed = false;
    let timer: ReturnType<typeof setTimeout>;
    const load = async () => {
      try {
        const snapshot = await (agentId ? api.agentProcesses(agentId) : api.processes());
        if (!disposed) {
          setProcesses(snapshot);
          setError(null);
        }
      } catch (e) {
        if (!disposed) setError(`Could not refresh processes: ${(e as Error).message}`);
      } finally {
        // Explicit starts/stops emit events, but a detached process can exit without one.
        if (!disposed) timer = setTimeout(load, 5000);
      }
    };
    load();
    return () => { disposed = true; clearTimeout(timer); };
  }, [api, agentId, tick, revision]);

  if (agentId && !processes?.length && !error) return null;
  const running = processes?.filter(p => p.state === 'running');
  const history = processes?.filter(p => p.state !== 'running');
  return (
    <div className={agentId ? 'agent-processes' : undefined}>
      <ProcessSection title="Running processes" rows={running} empty="No running processes." error={error} api={api}
        onSelect={agentId ? undefined : onSelect} onStop={() => setRevision(n => n + 1)} />
      <ProcessSection title="Process history" rows={history} empty="No exited or stopped processes." error={error} api={api}
        onSelect={agentId ? undefined : onSelect} />
    </div>
  );
}

function ProcessSection({ title, rows, empty, error, api, onSelect, onStop }: {
  title: string; rows?: BackgroundProcess[]; empty: string; error: string | null; api: Api;
  onSelect?: (id: string) => void; onStop?: () => void;
}) {
  const [expanded, setExpanded] = useState(false);
  const summary = useRef<HTMLElement>(null);
  return (
    <details className="process-section" onToggle={e => setExpanded(e.currentTarget.open)}>
      <summary ref={summary}>{title}{rows && ` (${rows.length})`}</summary>
      {expanded && <ProcessRows rows={rows} empty={empty} error={error} api={api} summary={summary} onSelect={onSelect} onStop={onStop} />}
    </details>
  );
}

function ProcessRows({ rows, empty, error, api, summary, onSelect, onStop }: {
  rows?: BackgroundProcess[]; empty: string; error: string | null; api: Api; summary: React.RefObject<HTMLElement>;
  onSelect?: (id: string) => void; onStop?: () => void;
}) {
  const [openLog, setOpenLog] = useState<string | null>(null);
  useEffect(() => {
    if (openLog && !rows?.some(p => processKey(p) === openLog)) setOpenLog(null);
  }, [rows, openLog]);
  return <>
    {error && <div className="error small" role="status">{error}</div>}
    {!error && !rows && <div className="muted small">Loading processes…</div>}
    {!error && rows?.length === 0 && <div className="muted small">{empty}</div>}
    {rows?.map(p => <ProcessRow key={processKey(p)} api={api} process={p} summary={summary} onSelect={onSelect} onStop={onStop}
      showLog={openLog === processKey(p)} toggleLog={() => setOpenLog(openLog === processKey(p) ? null : processKey(p))} />)}
  </>;
}

const processKey = (p: BackgroundProcess) => `${p.agentId}#${p.id}`;

function ProcessRow({ api, process: p, summary, onSelect, onStop, showLog, toggleLog }: {
  api: Api; process: BackgroundProcess; summary: React.RefObject<HTMLElement>; onSelect?: (id: string) => void;
  onStop?: () => void; showLog: boolean; toggleLog: () => void;
}) {
  const logId = useId();
  const row = useRef<HTMLDivElement>(null);
  const [stopping, setStopping] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useLayoutEffect(() => {
    const el = row.current;
    return () => {
      // An automatic exit/stop can remove the currently focused Stop or Log control.
      if (el?.contains(document.activeElement)) summary.current?.focus();
    };
  }, [summary]);

  const stop = async () => {
    if (stopping || !onStop) return;
    setStopping(true);
    setError(null);
    try {
      await api.stopProcess(p.agentId, p.id);
      onStop();
    } catch (e) {
      setError(`Could not stop process: ${(e as Error).message}`);
    } finally {
      setStopping(false);
    }
  };

  return (
    <div className="inbox-item process-row" ref={row} title={`Log: ${p.logPath}`}>
      <div className="row">
        <div>
          <span className={`dot ${p.state === 'running' ? 'running' : p.state === 'exited' ? 'error' : 'idle'}`} />{' '}
          <strong>#{p.id} {p.name}</strong> <span className="muted small">pid {p.pid} · {p.state} · started {new Date(p.started).toLocaleString()}</span>
          {onSelect && <> <span className="muted small">· by</span> <button className="link small" onClick={() => onSelect(p.agentId)}>{p.agentName}</button></>}
        </div>
        <div className="process-actions">
          <button className="link small" aria-expanded={showLog} aria-controls={showLog ? logId : undefined}
            aria-label={`${showLog ? 'Hide log for' : 'Log for'} ${p.name}`} onClick={toggleLog}>{showLog ? 'Hide log' : 'Log'}</button>
          {p.state === 'running' && <button className="link small" aria-disabled={stopping} aria-label={`Stop ${p.name}`}
            title="Stop it and everything it spawned" onClick={stop}>{stopping ? 'Stopping…' : 'Stop'}</button>}
        </div>
      </div>
      <div className="small muted"><code>{p.command}</code> in {p.workingDirectory}</div>
      {error && <div className="error small" role="alert">{error}</div>}
      {showLog && <ProcessLog api={api} process={p} id={logId} />}
    </div>
  );
}

function ProcessLog({ api, process: { agentId, id, name, state }, id: logId }: { api: Api; process: BackgroundProcess; id: string }) {
  const [log, setLog] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    let disposed = false;
    let timer: ReturnType<typeof setTimeout>;
    const load = async () => {
      try {
        const text = await api.processLog(agentId, id);
        if (!disposed) { setLog(text); setError(null); }
      } catch (e) {
        if (!disposed) setError((e as Error).message);
      } finally {
        // Only visible logs for running rows are tailed; historical logs are read once.
        if (!disposed && state === 'running') timer = setTimeout(load, 2000);
      }
    };
    load();
    return () => { disposed = true; clearTimeout(timer); };
  }, [api, agentId, id, state]);

  return <div id={logId}>
    {error && <div className="error small" role="status">{error}</div>}
    {log !== null && <pre className="output log-tail" role="region" aria-label={`Log for ${name}`} tabIndex={0}>{log || '(empty)'}</pre>}
    {!error && log === null && <div className="muted small">Loading log…</div>}
  </div>;
}
