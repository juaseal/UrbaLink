// Exercises the shipped web code with an in-memory DOM and API responses.
import {readFileSync} from 'node:fs';
import {runInNewContext} from 'node:vm';
import assert from 'node:assert/strict';
import {webcrypto} from 'node:crypto';
class Element {
 constructor(tag){this.tagName=tag;this.children=[];this.parentNode=null;this.hidden=false;this.disabled=false;this._value='';this._text='';this.files=[];this.classList={add(){},contains(){return false;}};}
 append(...nodes){for(const n of nodes){this.children.push(n);if(typeof n==='object')n.parentNode=this;}}
 replaceChildren(...nodes){this.children=[];this._text='';this.append(...nodes);}
 set textContent(v){this._text=v;this.children=[];}
 get textContent(){return this._text+this.children.map(n=>n.textContent||'').join('');}
 set value(v){this._value=v;}
 get value(){return this.tagName==='select'&&!this._value?this.children[0]?.value||'':this._value;}
 closest(){return this.parentNode||this;}
 querySelector(tag){return this.children.find(n=>n.tagName===tag);}
 addEventListener(){} setAttribute(){} removeAttribute(){} focus(){} close(){this.open=false;} showModal(){this.open=true;}
}
const nodes=new Map(),calls=[];
let response=()=>({items:[],nextBefore:null});
const document={getElementById:id=>{if(!nodes.has(id))nodes.set(id,new Element('div'));return nodes.get(id);},createElement:tag=>new Element(tag),querySelectorAll:()=>[],addEventListener(){},hidden:false};
const location={hash:'#home'},community={id:'community-1',name:'Los Olivos',state:'Active',role:'Member'};
const context={document,location,history:{pushState(a,b,url){location.hash=url;},replaceState(a,b,url){location.hash=url;}},window:{addEventListener(){},scrollTo(){}},localStorage:{getItem(){},setItem(){}},setInterval(){},crypto:webcrypto,URLSearchParams,Intl,Date,Map,Error,console,fetch:async(path,options)=>{calls.push({path,method:options.method,body:options.body?JSON.parse(options.body):undefined});const data=response(path,options);return {ok:true,status:data===null?204:200,json:async()=>data};}};
const path=new URL('../src/UrbaLink.Api/wwwroot/assets/app.js',import.meta.url);
const source=readFileSync(path,'utf8').replace('await run(boot);','');
runInNewContext(source+'\naccount={id:"viewer",name:"Vecino"};community='+JSON.stringify(community)+';communities=[community];globalThis.test={render,catalog,catalogDetail,catalogForm,view,run};',context);
const {test}=context;
function descendants(n){return [n,...n.children.filter(x=>typeof x==='object').flatMap(descendants)];}
const find=tag=>descendants(test.view).filter(n=>n.tagName===tag);
const input=label=>descendants(test.view).find(n=>n.tagName==='label'&&n._text===label)?.children[0];
async function submit(){await find('form')[0].onsubmit({preventDefault(){}});await new Promise(resolve=>setImmediate(resolve));assert.equal(nodes.get('feedback').textContent,'');}
function mock(fn){calls.length=0;response=fn;}
const item={id:7,ownerId:'seller',ownerName:'Alba',title:'Bicicleta',description:'Buen estado',mode:'Sale',priceCents:2500,state:'Active',highestBidCents:null,bidCount:0,photo:null};
const notice={id:8,authorId:'viewer',authorName:'Vecino',title:'Perro perdido',description:'Collar azul',kind:'Lost',species:'Dog',name:'Toby',location:'Parque',occurredAt:'2026-10-22T10:00:00Z',status:'Active',statusLabel:'Activo',version:2,photo:null};
mock(()=>({items:[item],nextBefore:7}));location.hash='#market?mode=Sale';await test.render();assert.equal(calls[0].path,'/api/communities/community-1/items?mode=Sale');assert.match(test.view.textContent,/Bicicleta/);await find('button').find(n=>n.textContent==='Ver anteriores').onclick();assert.equal(calls.at(-1).path,'/api/communities/community-1/items?mode=Sale&before=7');
mock(()=>({items:[notice],nextBefore:null}));location.hash='#animals?closed=true';await test.render();assert.equal(calls[0].path,'/api/communities/community-1/animals?closed=true');assert.match(test.view.textContent,/Perro perdido/);
mock(()=>({item,conversations:[]}));location.hash='#c/community-1/items/7';await test.render();assert.match(test.view.textContent,/Contactar en privado/);
mock(path=>path.endsWith('/conversations')?{id:10}:{item,conversations:[{id:10,name:'Alba'}],items:[],nextBefore:null});await find('button').find(n=>n.textContent==='Contactar en privado').onclick();assert.equal(calls[0].path,'/api/communities/community-1/items/7/conversations');assert.equal(calls[0].body.interestedUserId,null);assert.equal(location.hash,'#c/community-1/chat/Object/7/10');
mock(()=>({notice,conversations:[]}));location.hash='#c/community-1/animals/8';await test.render();assert.match(test.view.textContent,/Editar aviso/);assert.match(test.view.textContent,/Ha vuelto a casa/);
mock(path=>path.includes('/items/')?{item,conversations:[]}:{...item,id:9});test.catalogForm('items');input('Título').value='Bicicleta';input('Descripción').value='Buen estado';input('Precio o salida (€)').value='25.50';await submit();let sent=calls.find(c=>c.method==='POST');assert.equal(sent.body.priceCents,2550);assert.equal(sent.body.mode,'Sale');assert.equal(sent.body.endsAt,null);assert.equal(sent.body.period,null);assert.ok(sent.body.requestId);
mock(path=>path.includes('/items/')?{item,conversations:[]}:{...item,id:9});test.catalogForm('items');input('Título').value='Taladro';input('Descripción').value='Con brocas';input('Modalidad').value='Rent';input('Modalidad').onchange();input('Periodo de alquiler').value='Day';input('Precio o salida (€)').value='5';await submit();sent=calls.find(c=>c.method==='POST');assert.equal(sent.body.period,'Day');assert.equal(sent.body.priceCents,500);
mock(path=>path.includes('/items/')?{item,conversations:[]}:{...item,id:9});test.catalogForm('items');input('Título').value='Mesa';input('Descripción').value='Madera';input('Modalidad').value='Auction';input('Modalidad').onchange();input('Fin de subasta').value='2026-10-30T18:00';input('Incremento mínimo de puja (€)').value='2.50';await submit();sent=calls.find(c=>c.method==='POST');assert.equal(sent.body.bidStepCents,250);assert.ok(sent.body.endsAt);
mock(path=>path.includes('/animals/')?{notice,conversations:[]}:notice);test.catalogForm('animals');input('Zona aproximada').value='Parque';input('Descripción').value='Collar azul';await submit();sent=calls.find(c=>c.method==='POST');assert.equal(sent.body.kind,'Lost');assert.equal(sent.body.species,'Dog');assert.equal(sent.body.isWithReporter,false);assert.ok(sent.body.occurredAt);
mock((path,options)=>options.method==='PUT'?null:{notice,conversations:[]});test.catalogForm('animals',notice);input('Descripción').value='Collar rojo';await submit();sent=calls.find(c=>c.method==='PUT');assert.equal(sent.path,'/api/communities/community-1/animals/8?version=2');assert.equal(sent.body.description,'Collar rojo');
const auction={...item,mode:'Auction',highestBidCents:2500,bidStepCents:100,bidCount:1};mock(()=>({item:auction,conversations:[]}));await test.catalogDetail('items',7);assert.equal(input('Tu puja (€)').value,'26.00');await submit();sent=calls.find(c=>c.method==='POST');assert.equal(sent.path,'/api/communities/community-1/items/7/bids');assert.equal(sent.body.amountCents,2600);
const html=readFileSync(new URL('../src/UrbaLink.Api/wwwroot/index.html',import.meta.url),'utf8');assert.match(html,/href="#market"/);assert.match(html,/href="#animals"/);
console.log('OK: catalog routes, filters, pagination, details, private contact, sale/rent/auction forms, animal creation/editing, bid amounts and navigation.');
