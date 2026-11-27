import { useEffect, useRef, useState } from 'react';
import { Api, ArtifactRef } from './api';

export function ArtifactCards({ items, api, agentId }: { items?: ArtifactRef[]; api: Api; agentId: string }) {
  return items?.map(item => <ArtifactCard key={`${agentId}/${item.id}`} item={item} api={api} agentId={agentId} />);
}

function ArtifactCard({ item, api, agentId }: { item: ArtifactRef; api: Api; agentId: string }) {
  const [preview, setPreview] = useState<{ image: string } | { text: string; truncated: boolean }>();
  const [pending, setPending] = useState(false);
  const [error, setError] = useState('');
  const request = useRef<AbortController>();
  const image = useRef<string>();
  const clearImage = () => { if (image.current) URL.revokeObjectURL(image.current); image.current = undefined; };
  useEffect(() => () => { request.current?.abort(); clearImage(); }, [api, agentId, item.id]);

  const fetchItem = async (show: boolean) => {
    if (request.current) return;
    const controller = new AbortController();
    request.current = controller;
    setPending(true);
    setError('');
    try {
      const payload = await api.artifact(agentId, item, show, controller.signal);
      if (controller.signal.aborted) return;
      if (show) {
        clearImage();
        if (payload.blob) {
          image.current = URL.createObjectURL(payload.blob);
          setPreview({ image: image.current });
        } else if (payload.text !== undefined) setPreview({ text: payload.text, truncated: payload.truncated });
      } else if (payload.blob) {
        const url = URL.createObjectURL(payload.blob);
        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = item.name;
        document.body.append(anchor);
        anchor.click();
        anchor.remove();
        window.setTimeout(() => URL.revokeObjectURL(url), 30_000);
      }
    } catch (e) {
      if (!controller.signal.aborted) setError(e instanceof Error ? e.message : 'Artifact request failed');
    } finally {
      if (request.current === controller) {
        request.current = undefined;
        if (!controller.signal.aborted) setPending(false);
      }
    }
  };

  return <div className="tool" aria-label={`Artifact: ${item.name}`}>
    <div className="row small"><strong>{item.name}</strong> <span className="muted">{item.bytes.toLocaleString()} bytes</span>
      {preview ? <button className="small" onClick={() => { clearImage(); setPreview(undefined); }}>Hide preview</button> :
        (item.previewKind === 'text' || item.previewKind === 'png') && <button className="small" disabled={pending} onClick={() => void fetchItem(true)}>Preview</button>}
      <button className="small" disabled={pending} onClick={() => void fetchItem(false)}>Download</button>
      {pending && <button className="small" onClick={() => { request.current?.abort(); request.current = undefined; setPending(false); }}>Cancel</button>}
    </div>
    {error && <div className="error small" role="status">{error}</div>}
    {preview && ('image' in preview ? <img className="tool-image" src={preview.image} alt={item.name} onError={() => { clearImage(); setPreview(undefined); setError('Image preview could not be decoded'); }} /> :
      <><pre className="output">{preview.text}</pre>{preview.truncated && <div className="small muted">Preview truncated · download to see the full file</div>}</>)}
  </div>;
}
