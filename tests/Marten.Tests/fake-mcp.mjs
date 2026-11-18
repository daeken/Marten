// A tiny MCP server over stdio for tests. Each start is appended to $FAKE_MCP_STARTS.
import { appendFileSync } from 'node:fs';
import { createInterface } from 'node:readline';

if (process.env.FAKE_MCP_STARTS) appendFileSync(process.env.FAKE_MCP_STARTS, `${process.pid}\n`);
const PNG = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==';
const tools = [
  { name: 'echo', description: 'Echoes text back.', inputSchema: { type: 'object', properties: { text: { type: 'string' } }, required: ['text'] } },
  { name: 'pixel', description: 'Returns a one-pixel image.', inputSchema: { type: 'object', properties: {} } },
  { name: 'fail', description: 'Always fails.', inputSchema: { type: 'object', properties: {} } },
];
const send = msg => process.stdout.write(JSON.stringify({ jsonrpc: '2.0', ...msg }) + '\n');
createInterface({ input: process.stdin }).on('line', line => {
  const msg = JSON.parse(line);
  if (msg.id === undefined) return;
  switch (msg.method) {
    case 'initialize':
      return send({ id: msg.id, result: { protocolVersion: msg.params.protocolVersion, capabilities: { tools: {} }, serverInfo: { name: 'fake', version: '1' } } });
    case 'tools/list':
      // Page the list to exercise cursors.
      return msg.params?.cursor
        ? send({ id: msg.id, result: { tools: tools.slice(2) } })
        : send({ id: msg.id, result: { tools: tools.slice(0, 2), nextCursor: 'more' } });
    case 'tools/call': {
      const { name, arguments: args } = msg.params;
      if (name === 'echo') return send({ id: msg.id, result: { content: [{ type: 'text', text: `${args.text} (pid ${process.pid})` }] } });
      if (name === 'pixel') return send({ id: msg.id, result: { content: [{ type: 'text', text: 'here' }, { type: 'image', data: PNG, mimeType: 'image/png' }] } });
      return send({ id: msg.id, result: { content: [{ type: 'text', text: 'nope' }], isError: true } });
    }
    default:
      return send({ id: msg.id, error: { code: -32601, message: 'Method not found' } });
  }
});
