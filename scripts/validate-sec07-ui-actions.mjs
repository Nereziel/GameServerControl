import fs from 'node:fs';
import vm from 'node:vm';

const source = fs.readFileSync('src/GameServerControl.Agent/WebUi/core.js', 'utf8');
const start = source.indexOf('const uiAction=');
const end = source.indexOf('let CFG=', start);
if (start < 0 || end < 0) throw new Error('Could not isolate uiAction implementation.');
const helperSource = source.slice(start, end);

const listeners = new Map();
const calls = [];
const context = vm.createContext({
  console,
  encodeURIComponent,
  decodeURIComponent,
  JSON,
  Math,
  Date,
  crypto: { randomUUID: () => 'fixed-capability' },
  document: {
    addEventListener(type, fn) { listeners.set(type, fn); },
    querySelector(selector) { return selector === '#broadcast' ? { value: 'hello players' } : null; }
  },
  esc: s => (s == null ? '' : String(s)).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]))
});
context.globalThis = context;
context.probe = (...args) => { calls.push(args); };
vm.runInContext(helperSource, context);

const payload = `x\" onclick=\"alert(1)<script>`;
vm.runInContext(`globalThis.attrs = uiAction('probe', [${JSON.stringify(payload)}]);`, context);
const attrs = context.attrs;
if (/\bonclick\s*=/i.test(attrs) || attrs.includes('<script>') || attrs.includes(payload))
  throw new Error(`uiAction emitted unsafe raw handler/payload markup: ${attrs}`);
if (!attrs.includes('data-gsc-cap="fixed-capability"') || !attrs.includes('data-gsc-action="probe"'))
  throw new Error('uiAction did not emit capability-bound delegated action attributes.');

function attrMap(text) {
  const map = new Map();
  for (const match of text.matchAll(/([a-z0-9-]+)="([^"]*)"/gi)) map.set(match[1], match[2]);
  return map;
}
function elementFrom(text, overrides = {}) {
  const attrs = attrMap(text);
  return {
    disabled: false,
    value: '',
    checked: false,
    getAttribute(name) { return attrs.get(name) ?? null; },
    closest() { return this; },
    ...overrides
  };
}

const clickHandler = listeners.get('click');
if (!clickHandler) throw new Error('Delegated click listener was not installed.');
const trusted = elementFrom(attrs);
await clickHandler({ type: 'click', target: trusted });
if (calls.length !== 1 || calls[0][0] !== payload)
  throw new Error('Trusted delegated action did not round-trip its encoded argument.');

const forged = elementFrom(attrs.replace('fixed-capability', 'forged-capability'));
await clickHandler({ type: 'click', target: forged });
if (calls.length !== 1) throw new Error('Delegated action accepted a forged capability token.');

vm.runInContext(`globalThis.keyAttrs = uiAction('probe', ['enter'], {event:'keydown', key:'Enter'});`, context);
const keyEl = elementFrom(context.keyAttrs);
const keyHandler = listeners.get('keydown');
await keyHandler({ type: 'keydown', key: 'Escape', target: keyEl });
if (calls.length !== 1) throw new Error('Key-filtered action fired for the wrong key.');
await keyHandler({ type: 'keydown', key: 'Enter', target: keyEl });
if (calls.length !== 2 || calls[1][0] !== 'enter')
  throw new Error('Key-filtered action did not fire for Enter.');

console.log('SEC-07 delegated UI action behavior OK.');
