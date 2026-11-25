import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
const root='src/GameServerControl.Agent/WebUi/';
const source=fs.readFileSync(root+'i18n.js','utf8');
const html=fs.readFileSync(root+'index.html','utf8');
assert.match(html,/id="loginLanguage" data-gsc-locale/);
assert.match(html,/id="sidebarLanguage" data-gsc-locale/);
assert.ok(html.indexOf('/webui/i18n.js')<html.indexOf('/webui/core.js'));
assert.doesNotMatch(source,/\.innerHTML\s*=|eval\s*\(/);

class Element {
  constructor(text='',attrs={},value='') {
    this.nodeType=1;this.textContent=text;this.attrs=new Map(Object.entries(attrs));this.children=[];this.value=value;
  }
  getAttribute(k){return this.attrs.get(k)??null}
  setAttribute(k,v){this.attrs.set(k,v)}
  hasAttribute(k){return this.attrs.has(k)}
  removeAttribute(k){this.attrs.delete(k)}
  matches(s){return s.split(',').some(x=>this.attrs.has(x.slice(1,-1)))}
  querySelectorAll(s){return this.children.flatMap(x=>[...(x.matches(s)?[x]:[]),...x.querySelectorAll(s)])}
}
function fixture(initial) {
  const login=new Element('Sign in',{'data-gsc-i18n':'Sign in'});
  const placeholder=new Element('',{'placeholder':'Search servers or games…','data-gsc-i18n-placeholder':'Search servers or games…'},'my server name');
  const username=new Element('Start');
  const reused=new Element('My Agent',{'data-gsc-i18n':'This Agent'});
  const selected=new Element('All computers',{'data-gsc-i18n':'All computers',value:'all'},'all');
  const en=new Element('',{'data-gsc-locale':''},'en');
  const sidebar=new Element('',{'data-gsc-locale':''},'en');
  const body=new Element();body.children=[login,placeholder,username,reused,selected,en,sidebar];
  let change,observe;
  const doc={readyState:'complete',nodeType:9,body,documentElement:{},title:'',
    querySelectorAll:s=>body.querySelectorAll(s),addEventListener:(name,fn)=>{if(name==='change')change=fn}};
  const storage=new Map(initial?[['gsc.ui.language.v1',initial]]:[]);
  const ctx={document:doc,localStorage:{getItem:k=>storage.get(k),setItem:(k,v)=>storage.set(k,v)},
    MutationObserver:class{constructor(cb){observe=cb}observe(){}}};
  vm.createContext(ctx);vm.runInContext(source,ctx);
  return {api:ctx.GSC_I18N,doc,login,placeholder,username,reused,selected,en,sidebar,storage,change,observe};
}
const f=fixture();
assert.equal(f.doc.documentElement.lang,'en');
assert.equal(f.login.textContent,'Sign in');
f.en.value='cs';f.change({target:f.en});
assert.equal(f.doc.documentElement.lang,'cs');
assert.equal(f.login.textContent,'Přihlásit se');
assert.equal(f.sidebar.value,'cs');
assert.equal(f.placeholder.getAttribute('placeholder'),'Hledat servery nebo hry…');
assert.equal(f.placeholder.value,'my server name');
assert.equal(f.username.textContent,'Start','unmarked user data must not translate');
assert.equal(f.reused.textContent,'My Agent','marked element reused for dynamic data must not change');
assert.equal(f.selected.value,'all');
assert.equal(f.api.t('not in catalog'),'not in catalog');
const added=new Element('Backups',{'data-gsc-i18n':'Backups'});
f.observe([{addedNodes:[added]}]);assert.equal(added.textContent,'Zálohy');
f.api.set(f.login,'Signing in…');assert.equal(f.login.textContent,'Přihlašování…');
f.api.setLocale('en');
assert.equal(f.login.textContent,'Signing in…');assert.equal(f.sidebar.value,'en');
assert.equal(f.placeholder.getAttribute('placeholder'),'Search servers or games…');
assert.equal(f.storage.size,1);
assert.equal(fixture('cs').login.textContent,'Přihlásit se');
assert.equal(fixture('unexpected').doc.documentElement.lang,'en');
const decode=s=>s.replace(/&quot;/g,'"').replace(/&#39;/g,"'").replace(/&lt;/g,'<').replace(/&amp;/g,'&');
f.api.setLocale('cs');
for(const name of fs.readdirSync(root).filter(n=>/\.(js|html)$/.test(n)&&n!=='i18n.js')){
  const text=fs.readFileSync(root+name,'utf8');
  for(const match of text.matchAll(/data-gsc-i18n(?:-(?:title|placeholder|aria-label))?="([^"\n]+)"/g)){
    const key=decode(match[1]);
    if(key.includes('$')||key.includes("'+"))continue;
    // Technical terms are deliberately identical in both locales.
    if(['API token','Online','Offline','Dialog','Discord webhook','Discord webhook (HTTPS)','Port','Role','Metadata','restart'].includes(key))continue;
    assert.notEqual(f.api.t(key),key,'Missing Czech translation for '+key+' in '+name);
  }
}
console.log('Locale selection, dynamic markup and safe fallback checks passed.');
