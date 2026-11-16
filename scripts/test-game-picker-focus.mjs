import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';

// Execute the real game picker functions with a small DOM stub to verify that
// typing only updates results and does not recreate/focus-loss the input.
const source = fs.readFileSync('src/GameServerControl.Agent/WebUi/server-admin.js', 'utf8');
const start = source.indexOf('function openCreate(){');
const end = source.indexOf('function openCreateSettings(', start);
assert.ok(start >= 0 && end > start, 'Cannot find game picker implementation');

let modalRenders = 0;
let input = null;
let results = null;
const games = [
  {key:'minecraft-paper',label:'Minecraft Paper',appId:null,icon:'M',size:'1GB'},
  {key:'valheim',label:'Valheim',appId:'896660',icon:'V',size:'2GB'},
  {key:'minecraft-fabric',label:'Minecraft Fabric',appId:null,icon:'F',size:'1GB'}
];
const context = {
  GAMES: games,
  esc: value => String(value ?? '').replaceAll('&','&amp;').replaceAll('<','&lt;'),
  uiAction: () => 'data-gsc-action="test"',
  openModal: (title, html) => {
    assert.equal(title, 'Create server');
    modalRenders++;
    input = {value:'',isFocused:false,focus(){this.isFocused = true}};
    results = {innerHTML:html.match(/<div id="gamePickerResults" class="games">([\\s\\S]*?)<\\/div>\\s*$/)?.[1] ?? ''};
  },
  $: selector => selector === '#gameSearch' ? input : selector === '#gamePickerResults' ? results : null
};
vm.createContext(context);
vm.runInContext(source.slice(start,end),context);
context.openCreate();
assert.equal(modalRenders,1);
const initialInput = input;
assert.equal(initialInput.isFocused,true,'Newly opened picker should focus search');

for(const query of ['m','mi','min','mine','minec','minecr','minecra','minecraf','minecraft']){
  input.value = query;
  context.filterGamePicker(query);
  assert.equal(input,initialInput,'Search input must not be replaced while typing');
  assert.equal(input.isFocused,true,'Search input must stay focused');
  assert.equal(modalRenders,1,'Search may not reopen modal on input');
  assert.ok(results.innerHTML.includes('Minecraft Paper'),'Matches should remain visible');
  assert.ok(!results.innerHTML.includes('Valheim'),'Unrelated games should be filtered out');
}
context.filterGamePicker('not-a-game');
assert.ok(results.innerHTML.includes('No matching games'));
context.filterGamePicker('');
assert.ok(results.innerHTML.includes('Valheim'),'Clearing search should restore all results');
assert.equal(modalRenders,1);

console.log('Create server game picker focus/filter regression OK.');
