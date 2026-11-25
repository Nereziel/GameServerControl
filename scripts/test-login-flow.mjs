import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const source=fs.readFileSync('src/GameServerControl.Agent/WebUi/core.js','utf8');
const html=fs.readFileSync('src/GameServerControl.Agent/WebUi/index.html','utf8');
const adapter=fs.readFileSync('src/GameServerControl.Cloud/static/cloud-adapter.js','utf8');
const auth=fs.readFileSync('src/GameServerControl.Agent/WebUi/server-admin.js','utf8');
const segment=(start,end)=>{
  const a=source.indexOf(start),b=source.indexOf(end,a+1);
  assert(a>=0&&b>a,'Login source boundaries');
  return source.slice(a,b);
};
const code=segment('let LOGIN_PENDING=false;','async function logout(){')+
  segment('async function init(){',"window.addEventListener('DOMContentLoaded',init);");
assert.match(html,/<form id="loginForm"[^>]*novalidate/);
assert.match(html,/<div id="loginError"[^>]*role="alert"/);
assert.match(html,/<button id="loginConnectBtn"[^>]*type="submit"/);
assert.match(html,/<details id="loginAdvanced"/);
assert.match(source,/bind\('#loginForm','submit'/);
assert.match(auth,/if\(identity\)\{AUTH=identity;window\.GSC_AUTH=AUTH\}else await loadAuthIdentity\(\)/);
assert.match(adapter,/await localBoot\(identity\)/);
assert.doesNotMatch(adapter,/loginBox\.innerHTML/);

function fixture({cloud=false,search='',stored=null}={}){
  const elements=new Map();
  for(const id of ['loginForm','loginUrl','loginToken','loginTarget','loginError','loginRememberBrowser','rememberBrowserField','loginTransportWarning','loginConnectBtn','loginAdvanced','loginLoading','loginHeading','loginDescription','loginFootnote','cloudLoginState']){
    const classes=new Set(['hidden']);
    elements.set('#'+id,{value:'',checked:false,disabled:false,type:'password',textContent:'',open:false,
      classList:{add:c=>classes.add(c),remove:c=>classes.delete(c),contains:c=>classes.has(c),toggle:(c,v)=>v?classes.add(c):classes.delete(c)},
      setAttribute(){},focus(){this.focused=true},querySelectorAll(){return ['loginToken','loginUrl','loginRememberBrowser','loginConnectBtn'].map(x=>elements.get('#'+x))}});
  }
  const get=id=>elements.get('#'+id);
  const storage=new Map(stored?[['cfg',JSON.stringify(stored)]]:[]);
  const calls=[],boots=[],remembers=[],messages=[],history=[];
  const ctx={URL,URLSearchParams,AbortController,TypeError,setTimeout,clearTimeout,console,
    $:s=>elements.get(s),window:{GSC_CLOUD_MODE:cloud},
    location:{origin:'http://localhost:5099',protocol:'http:',search,pathname:'/',hash:''},
    history:{replaceState:(...args)=>history.push(args)},
    localStorage:{removeItem(){}},sessionStorage:{setItem:(k,v)=>storage.set(k,v),getItem:k=>storage.get(k),removeItem:k=>storage.delete(k)},
    CFG:{url:'',token:''},CFG_KEY:'cfg',isLocalBrowserOrigin:()=>true,
    restoreBrowserLogin:async()=>false,
    rememberLocalBrowser:async()=>remembers.push(true),
    boot:async identity=>boots.push(identity),toast:(...args)=>messages.push(args),openTokens(){},
    fetch:async (url,opts)=>{calls.push({url,opts});return {ok:true,status:200,json:async()=>({role:'Admin',name:'Test'})}}
  };
  vm.createContext(ctx);vm.runInContext(code,ctx);
  return {ctx,get,storage,calls,boots,remembers,messages,history};
}
{
  const f=fixture();await f.ctx.init();
  assert.equal(f.calls.length,0,'fresh login must not make a speculative network request');
  assert.equal(f.get('loginUrl').value,'http://localhost:5099');
  assert.equal(f.get('loginForm').classList.contains('hidden'),false);
  f.get('loginToken').value='test-token';f.get('loginRememberBrowser').checked=true;
  await f.ctx.login();
  assert.equal(f.calls.length,1);
  assert.equal(f.calls[0].url,'http://localhost:5099/api/auth/me');
  assert.equal(f.calls[0].opts.redirect,'error');
  assert.equal(f.calls[0].opts.cache,'no-store');
  assert.equal(f.calls[0].opts.headers.Authorization,'Bearer test-token');
  assert.equal(f.boots.length,1);assert.equal(f.boots[0].role,'Admin');
  assert.equal(f.remembers.length,1);
  assert.equal(f.get('loginToken').value,'');
}
{
  const f=fixture();await f.ctx.init();f.get('loginToken').value='bad-token';
  f.ctx.fetch=async()=>({status:401,ok:false});
  await f.ctx.login();
  assert.match(f.get('loginError').textContent,/token was rejected/);
  assert.equal(f.storage.has('cfg'),false);assert.equal(f.boots.length,0);
  assert.equal(f.get('loginConnectBtn').disabled,false);
}
{
  const f=fixture();await f.ctx.init();f.get('loginToken').value='test-token';
  f.get('loginUrl').value='https://another.example';f.get('loginRememberBrowser').checked=true;
  f.ctx.updateLoginTarget();
  assert.equal(f.get('loginRememberBrowser').checked,false);
  assert.equal(f.get('rememberBrowserField').classList.contains('hidden'),true);
  let resolve;f.ctx.fetch=()=>new Promise(r=>{resolve=r});
  const first=f.ctx.login(),second=f.ctx.login();
  assert.equal(f.get('loginConnectBtn').disabled,true);
  resolve({ok:true,status:200,json:async()=>({role:'ReadOnly'})});
  await Promise.all([first,second]);assert.equal(f.boots.length,1);assert.equal(f.remembers.length,0);
}
{
  const f=fixture({stored:{url:'http://localhost:5099',token:'saved'}});
  await f.ctx.init();assert.equal(f.calls.length,1);assert.equal(f.boots.length,1);
}
{
  const f=fixture({cloud:true});await f.ctx.init();
  assert.equal(f.calls.length,1);assert.equal(f.boots.length,1);
  assert.equal(f.get('loginForm').classList.contains('hidden'),true);
  assert.equal(f.calls[0].opts.headers.Authorization,'Bearer cloud-gateway');
}
{
  const f=fixture({cloud:true});f.ctx.fetch=async()=>({ok:false,status:403});
  await f.ctx.init();assert.equal(f.boots.length,0);
  assert.equal(f.get('cloudLoginState').classList.contains('hidden'),false);
  assert.equal(f.get('loginForm').classList.contains('hidden'),true);
}
{
  const f=fixture({search:'?token=legacy&url=https%3A%2F%2Fanother.example'});
  await f.ctx.init();assert.equal(f.calls.length,0);assert.equal(f.history.length,1);
  assert.equal(f.storage.has('cfg'),false);
}
{
  const f=fixture();await f.ctx.init();f.get('loginUrl').value='https://user:pass@example.test';
  f.get('loginToken').value='test-token';await f.ctx.login();
  assert.equal(f.calls.length,0);assert.match(f.get('loginError').textContent,/without credentials/);
}
{
  const f=fixture();await f.ctx.init();f.get('loginToken').value='test-token';
  f.ctx.fetch=async()=>({ok:true,status:200,json:async()=>({role:'unexpected'})});
  await f.ctx.login();assert.equal(f.boots.length,0);assert.match(f.get('loginError').textContent,/valid Agent identity/);
}
console.log('Login flow regression checks passed.');
