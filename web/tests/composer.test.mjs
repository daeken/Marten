import assert from 'node:assert/strict';
import { setTimeout as delay } from 'node:timers/promises';
import { test } from 'node:test';
import { JSDOM } from 'jsdom';
import { act, createElement } from 'react';
import { Api } from '../src/api';
import { Composer, ComposerDrafts } from '../src/Composer';

const dom = new JSDOM('<!doctype html><main></main>', { url: 'http://localhost/' });
globalThis.window = dom.window;
globalThis.document = dom.window.document;
globalThis.IS_REACT_ACT_ENVIRONMENT = true;
const { createRoot } = await import('react-dom/client');
const main = document.querySelector('main');
const field = () => main.querySelector('textarea');
const queued = () => main.querySelector('button.pending-row');
const row = { id: 'q', text: 'Queued preview', mode: 'queued' };
const steer = { id: 's', text: 'Steering preview', mode: 'immediate' };
async function until(check) {
  for (let i = 0; i < 100; i++) {
    if (check()) return;
    await act(() => delay(10));
  }
  assert.ok(check(), 'Expected the UI or request state to change');
}
function setup(t, fetch) {
  const prior = globalThis.fetch;
  globalThis.fetch = fetch;
  const root = createRoot(main);
  const drafts = new ComposerDrafts();
  const api = new Api('test');
  t.after(async () => {
    await act(() => root.unmount());
    globalThis.fetch = prior;
  });
  return {
    drafts,
    render: (id, pendingTick = 0) => act(() => root.render(createElement(Composer, { key: id, id, api, busy: true, pendingTick, drafts }))),
  };
}

test('only queued rows recall; draft lock survives navigation and uses the returned text', async t => {
  let rows = [row, steer];
  let calls = 0;
  let complete;
  const { drafts, render } = setup(t, async (path, opts) => {
    if (opts.method === 'GET') return Response.json(path.includes('/a/') ? rows : []);
    assert.equal(opts.method, 'DELETE');
    assert.equal(path, '/api/agents/a/pending-messages/q');
    assert.equal(opts.headers.Authorization, 'Bearer test');
    calls++;
    return new Promise(resolve => { complete = resolve; });
  });
  await render('a');
  await until(() => queued());
  assert.match(queued().textContent, /Queued · Edit.*Queued preview/);
  assert.match(main.querySelector('li:nth-child(2)').textContent, /Steering.*Steering preview/);
  assert.equal(main.querySelector('li:nth-child(2) button'), null);
  await act(() => drafts.edit('a', 'Existing draft'));
  assert.equal(field().value, 'Existing draft');
  assert.equal(queued().disabled, true);
  await act(() => queued().click());
  assert.equal(calls, 0);
  await act(() => drafts.edit('a', ''));
  await act(() => { queued().click(); queued().click(); });
  assert.equal(calls, 1);
  assert.equal(field().readOnly, true);
  await act(() => drafts.edit('a', 'Cannot race the recall'));
  await render('b');
  await act(() => drafts.edit('b', 'Different chat'));
  rows = [steer];
  await act(() => complete(Response.json({ ...row, text: 'Server returned text' })));
  assert.equal(field().value, 'Different chat');
  await render('a');
  assert.equal(field().value, 'Server returned text');
  await until(() => main.querySelector('li'));
  assert.equal(queued(), null);
  assert.match(main.querySelector('li').textContent, /Steering preview/);
});

test('404 and ambiguous network failure both preserve the draft and report the outcome', async t => {
  let all = [row];
  let failNetwork = false;
  const { render } = setup(t, async (_, opts) => {
    if (opts.method === 'GET') return Response.json(all);
    if (failNetwork) throw new TypeError('network disconnected');
    all = [];
    return new Response(null, { status: 404 });
  });
  await render('a');
  await until(() => queued());
  await act(() => queued().click());
  assert.match(main.textContent, /no longer available to recall/);
  assert.equal(field().value, '');
  await until(() => !queued());
  all = [row];
  failNetwork = true;
  await render('a', 1);
  await until(() => queued());
  await act(() => queued().click());
  assert.match(main.textContent, /Couldn’t confirm recall/);
  assert.equal(field().value, '');
  assert.equal(field().readOnly, false);
});

test('refreshes coalesce and discard stale snapshots and old-agent responses', async t => {
  const requests = [];
  const { render } = setup(t, (path, opts) => new Promise(resolve => requests.push({ path, signal: opts.signal, resolve })));
  await render('a');
  await until(() => requests.length === 1);
  await render('a', 1);
  await render('a', 2);
  await act(() => delay(60));
  assert.equal(requests.length, 1);
  await act(() => requests[0].resolve(Response.json([row])));
  assert.equal(queued(), null);
  await until(() => requests.length === 2);
  await act(() => requests[1].resolve(Response.json([steer])));
  assert.match(main.textContent, /Steering preview/);
  await render('a', 3);
  await until(() => requests.length === 3);
  await render('b', 3);
  assert.equal(requests[2].signal.aborted, true);
  await until(() => requests.length === 4);
  assert.equal(requests[3].path, '/api/agents/b/pending-messages');
  await act(() => requests[3].resolve(Response.json([])));
  await act(() => requests[2].resolve(Response.json([row])));
  assert.equal(main.querySelector('li'), null);
});
