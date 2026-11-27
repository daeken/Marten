import { readFileSync } from 'node:fs';
import assert from 'node:assert/strict';
import { test } from 'node:test';
import ts from 'typescript';

const source = readFileSync(new URL('../src/api.ts', import.meta.url), 'utf8');
const { outputText } = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext } });
const { Api, ArtifactUnavailable } = await import(`data:text/javascript;base64,${Buffer.from(outputText).toString('base64')}`);
const api = new Api('fixture-token');
const ref = { id: '0'.repeat(32), bytes: 3, name: 'demo.txt', previewKind: 'text' };
const signal = new AbortController().signal;
const media = { 'Content-Type': 'application/octet-stream' };

async function withResponse(response, fn) {
  const old = globalThis.fetch;
  globalThis.fetch = async (path, options) => {
    assert.equal(path, `/api/agents/a-01/artifacts/${ref.id}`);
    assert.equal(options.headers.Authorization, 'Bearer fixture-token');
    assert.equal(options.redirect, 'error');
    assert.equal(options.cache, 'no-store');
    assert.equal(options.signal, signal);
    return response;
  };
  try { return await fn(api.artifact('a-01', ref, false, signal)); }
  finally { globalThis.fetch = old; }
}

test('accepts an exact bounded body without Content-Length', async () => {
  await withResponse(new Response('abc', { headers: media }), async result => assert.equal(await (await result).blob.text(), 'abc'));
});
test('rejects an overlong body even when Content-Length claims the allowed size', async () => {
  await withResponse(new Response('abcd', { headers: { ...media, 'Content-Length': '3' } }), result => assert.rejects(result, /byte limit/));
});
test('rejects a disagreeing length header independently of validating body size', async () => {
  await withResponse(new Response('abc', { headers: { ...media, 'Content-Length': '2' } }), result => assert.rejects(result, /Incomplete/));
  await withResponse(new Response('abc', { headers: { ...media, 'Content-Length': '4' } }), result => assert.rejects(result, /byte limit/));
});
test('rejects incomplete content without a length header', async () => {
  await withResponse(new Response('ab', { headers: media }), result => assert.rejects(result, /Incomplete/));
});
test('separates missing snapshots from ordinary transport errors', async () => {
  await withResponse(new Response(null, { status: 404 }), result => assert.rejects(result, e => e instanceof ArtifactUnavailable && /backups restore the card/.test(e.message)));
});
