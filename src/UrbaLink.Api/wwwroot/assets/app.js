'use strict';
const $=id=>document.getElementById(id),view=$('view'),feedback=$('feedback');
const categories=[['General','General'],['Ayuda','Necesito ayuda'],['Ofrezco','Servicios'],['Recomendación','Recomendaciones'],['Material','Material'],['Coche','Trayectos'],['Aviso oficial','Aviso oficial']];
const label=key=>categories.find(x=>x[0]===key)?.[1]||key;
let account=null,communities=[],community=null,busy=false,modalAction=null,lastRoute='',pendingNavigation=false;
const drafts=new Map();
class ApiError extends Error{constructor(status,message){super(message);this.status=status;}}
async function api(path,method='GET',data){
 const response=await fetch(path,{method,credentials:'same-origin',cache:'no-store',headers:{'X-UrbaLink-Web':'1',...(data!==undefined?{'Content-Type':'application/json'}:{})},body:data!==undefined?JSON.stringify(data):undefined});
 if(!response.ok){let body={};try{body=await response.json();}catch{}throw new ApiError(response.status,body.message||(response.status===429?'Demasiados intentos. Espera un minuto.':'No se pudo completar la operación. Inténtalo de nuevo.'));}
 return response.status===204?null:response.json();
}
function el(tag,text,cls){const n=document.createElement(tag);if(text!==undefined)n.textContent=text;if(cls)n.className=cls;return n;}
function date(value){return new Date(value).toLocaleString('es-ES',{dateStyle:'medium',timeStyle:'short'});}
function link(text,route,cls='button secondary'){const a=el('a',text,cls);a.href='#'+route;return a;}
function button(text,fn,cls=''){const b=el('button',text,cls);b.type='button';b.onclick=()=>run(fn);return b;}
function text(value,cls=''){return el('p',value,cls);}
function field(form,title,type='text',value='',options={}){const l=el('label',title),n=el(type==='textarea'?'textarea':'input');if(type!=='textarea')n.type=type;n.value=value;for(const [k,v]of Object.entries(options))n[k]=v;l.append(n);form.append(l);return n;}
function form(submitLabel,action){const f=el('form'),submit=el('button',submitLabel);submit.type='submit';f.onsubmit=e=>{e.preventDefault();run(()=>action(f));};f.finish=()=>f.append(submit);return f;}
function card(title,body){const n=el('section',undefined,'card');if(title)n.append(el('h2',title));if(body)n.append(text(body,'muted'));return n;}
function heading(title,description){view.replaceChildren(el('h1',title));if(description)view.append(text(description,'muted'));}
function empty(message){return el('p',message,'empty');}
function root(c=community){return '/api/communities/'+c.id;}
function rememberCommunity(){try{localStorage.setItem('urbalink.community',community.id);}catch{}}
function currentRoute(){return location.hash.slice(1)||'home';}
async function go(route){history.pushState(null,'','#'+route);await render();window.scrollTo(0,0);view.focus({preventScroll:true});}
async function run(fn){
 if(busy){pendingNavigation=true;return;}busy=true;const fields=[...document.querySelectorAll('input,textarea,select:not(#community)')].map(n=>[n,n.disabled]);for(const [n]of fields)n.disabled=true;feedback.textContent='';$('modal-error').textContent='';document.querySelectorAll('button').forEach(b=>b.disabled=true);
 try{await fn();}catch(e){
  if(e.status===401 && account){reset();login();feedback.textContent='Tu sesión ha caducado. Vuelve a iniciar sesión.';}
  else{feedback.textContent=e instanceof ApiError?e.message:'No se pudo conectar. Comprueba tu conexión e inténtalo de nuevo.';if($('modal').open)$('modal-error').textContent=feedback.textContent;}
 }finally{busy=false;for(const [n,disabled]of fields)if(n.isConnected)n.disabled=disabled;document.querySelectorAll('button').forEach(b=>b.disabled=false);if(pendingNavigation){pendingNavigation=false;if(account && currentRoute()!==lastRoute)await run(render);}}
}
function reset(){account=null;communities=[];community=null;drafts.clear();view.replaceChildren();$('app').hidden=true;$('bell').hidden=true;$('modal').close();modalAction=null;}
function ask(title,body,confirm,action,reason=false){
 $('modal-title').textContent=title;$('modal-body').textContent=body;$('modal-submit').textContent=confirm;$('modal-error').textContent='';$('modal-fields').replaceChildren();
 let input;if(reason)input=field($('modal-fields'),'Motivo','textarea','',{required:true,minLength:5,maxLength:2000});
 modalAction=async()=>{await action(input?.value.trim());$('modal').close();};$('modal').showModal();
}
$('modal-cancel').onclick=()=>$('modal').close();$('modal-form').onsubmit=e=>{e.preventDefault();if(modalAction)run(modalAction);};
function login(register=false){
 $('starting').hidden=true;$('auth').hidden=false;const auth=$('auth');auth.replaceChildren();
 const grid=el('div',undefined,'intro-grid'),intro=el('section',undefined,'intro');intro.append(text('CERCA DE TI','eyebrow'),el('h1','Tu comunidad, más cerca.'),text('Pide ayuda, comparte lo que sabes y conversa con quienes viven a tu lado. Todo empieza con tus vecinos.'));
 const panel=card(register?'Crea tu cuenta':'Bienvenido a tu comunidad','Entra con tu cuenta de UrbaLink. Si ya usabas la app, puedes utilizar el mismo acceso.');let name,email,password;
 const f=form(register?'Crear cuenta':'Iniciar sesión',async()=>{
  account=await api(register?'/api/web/register':'/api/web/session','POST',{...(register?{name:name.value}:{}),email:email.value,password:password.value});password.value='';drafts.clear();await hydrate();
 });
 if(register)name=field(f,'Tu nombre','text','',{required:true,minLength:2,maxLength:80,autocomplete:'name'});
 email=field(f,'Correo electrónico','email','',{required:true,maxLength:254,autocomplete:'username'});
 password=field(f,'Contraseña','password','',{required:true,minLength:register?12:1,maxLength:128,autocomplete:register?'new-password':'current-password'});
 f.finish();panel.append(f,text('En este dispositivo conservaremos tu sesión para que no tengas que entrar cada vez.','muted'),button(register?'Ya tengo cuenta':'Quiero registrarme',async()=>login(!register),'ghost'));grid.append(intro,panel);auth.append(grid);
}
async function hydrate(){
 communities=await api('/api/communities');let saved;try{saved=localStorage.getItem('urbalink.community');}catch{}
 community=communities.find(c=>c.id===(community?.id||saved))||communities[0]||null;updateSelector();$('auth').hidden=true;$('starting').hidden=true;$('app').hidden=false;$('bell').hidden=false;await render();await refreshBell();
}
function updateSelector(){const select=$('community');select.replaceChildren();for(const c of communities){const option=el('option',c.name+(c.state!=='Active'?' · '+(c.state==='Pending'?'Pendiente':'Suspendida'):''));option.value=c.id;select.append(option);}select.value=community?.id||'';select.disabled=communities.length<2;select.closest('.communitybar').hidden=communities.length===0;}
$('community').onchange=()=>run(async()=>{community=communities.find(c=>c.id===$('community').value);rememberCommunity();await go('home');});
$('bell').onclick=()=>run(()=>go('messages'));
window.addEventListener('hashchange',()=>{if(account)run(render);});window.addEventListener('popstate',()=>{if(account && currentRoute()!==lastRoute)run(render);});
document.addEventListener('click',e=>{
 const a=e.target.closest('a[href^="#"]');if(!a)return;e.preventDefault();if(account)run(()=>go(a.getAttribute('href').slice(1)));else if(a.classList.contains('brand'))login();
});
async function render(){
 const route=currentRoute();lastRoute=route;const parts=route.split('?')[0].split('/');
 if(parts[0]==='c'){
  const selected=communities.find(c=>c.id===parts[1]);if(!selected)throw new ApiError(403,'No tienes acceso a esta comunidad. Comprueba tu cuenta o entra con una invitación.');community=selected;rememberCommunity();updateSelector();
 }
 const section=parts[0]==='c'?(parts[2]==='chat'?'messages':'board'):parts[0];document.querySelectorAll('[data-nav]').forEach(a=>{if(a.dataset.nav===section)a.setAttribute('aria-current','page');else a.removeAttribute('aria-current');});
 if(section==='messages' && parts[0]!=='c'){await messages();return;}
 if(section==='profile'){profile();return;}
 if(section==='join'){join();return;}
 if(section==='create-community'){createCommunity();return;}
 if(!community){welcome();return;}
 if(community.state!=='Active'){heading(community.name,community.state==='Pending'?'Tu comunidad está pendiente de aprobación por UrbaLink. Podrás invitar vecinos y publicar cuando se apruebe.':'Esta comunidad está suspendida por UrbaLink.');view.append(button('Actualizar estado',hydrate,'secondary'),link('Mi cuenta','profile','button ghost'));return;}
 if(parts[0]==='c' && parts[2]==='post' && /^\d+$/.test(parts[3])){await post(parts[3]);return;}
 if(parts[0]==='c' && parts[2]==='chat'){await conversation(parts);return;}
 if(section==='board'){await board(new URLSearchParams(route.split('?')[1]||'').get('category'));return;}
 if(section==='new-post'){newPost();return;}
 await home();
}
function welcome(){heading('Tu comunidad empieza aquí','Únete a tus vecinos con una invitación o solicita una nueva comunidad.');const actions=el('div',undefined,'actions');actions.append(link('Unirme con una invitación','join','button'),link('Solicitar comunidad','create-community'));view.append(actions);}
async function home(){
 const data=await api(root()+'/overview');heading('Hola, '+account.name);const hero=el('section',undefined,'welcome'),copy=el('div');copy.append(text('LA VIDA EN TU URBA','eyebrow'),el('h1','Un poco más cerca de tus vecinos.'),text(`${data.memberCount} vecinos · ${data.activePosts} publicaciones`));hero.append(copy,link('＋ Compartir algo','new-post'));view.append(hero);
 const grid=el('div',undefined,'grid');const boardCard=card('Lo que pasa cerca de ti','Preguntas, novedades y conversaciones de tu comunidad.');boardCard.append(link('Abrir el tablón','board','button ghost'));const help=card('Ayuda y servicios','Encuentra una mano, ofrece lo que sabes y comparte recomendaciones.');help.append(link('Explorar ayuda','board?category='+encodeURIComponent('Ayuda y servicios'),'button ghost'));grid.append(boardCard,help);view.append(grid);
 const head=el('div',undefined,'section-head');head.append(el('h2','Últimas novedades'),link('Ver todas','board','button ghost'));view.append(head);
 if(!data.recent.length)view.append(empty('Todavía no hay publicaciones. Comparte la primera.'));for(const p of data.recent)view.append(postCard(p));
}
function postCard(p){const n=el('article',undefined,'post');n.append(el('span',label(p.category),'badge'),el('h3'));n.querySelector('h3').append(link(p.title,`c/${p.communityId}/post/${p.id}`,''));n.append(text(p.body,'preview'),text(`${p.authorName} · ${date(p.createdAt)}`,'muted'),link(p.category==='Ofrezco'?'Ver servicio y contactar':'Ver publicación',`c/${p.communityId}/post/${p.id}`,'button ghost'));return n;}
async function board(category=null){
 const query=category?'?category='+encodeURIComponent(category):'';const data=await api(root()+'/posts'+query);heading('Tablón de '+community.name,'Lo que compartimos hace comunidad.');view.append(link('＋ Nueva publicación','new-post','button'));
 const filters=el('div',undefined,'filters');for(const [key,title]of [['','Todas'],['General','General'],['Ayuda y servicios','Ayuda y servicios'],['Coche','Trayectos'],['Aviso oficial','Avisos']])filters.append(link(title,'board'+(key?'?category='+encodeURIComponent(key):''),key===(category||'')?'button':'button secondary'));view.append(filters);
 const list=el('div');view.append(list);for(const p of data.items)list.append(postCard(p));if(!data.items.length)list.append(empty('No hay publicaciones en esta categoría.'));let next=data.nextBefore;
 const more=button('Ver publicaciones anteriores',async()=>{const page=await api(root()+'/posts'+query+(query?'&':'?')+'before='+next);for(const p of page.items)list.append(postCard(p));next=page.nextBefore;more.hidden=next===null;},'secondary');more.hidden=next===null;view.append(more);
}
function newPost(){
 heading('Comparte con tus vecinos','Las publicaciones y sus comentarios son visibles para tu comunidad. En Servicios, el contacto es privado.');const box=card();box.classList.add('narrow');
 const saved=drafts.get('new-post:'+community.id)||{};let title,body,category;
 const f=form('Publicar',async()=>{const p=await api(root()+'/posts','POST',{title:title.value,body:body.value,category:category.value});drafts.delete('new-post:'+community.id);await go(`c/${community.id}/post/${p.id}`);});
 title=field(f,'Título','text',saved.title||'',{required:true,minLength:3,maxLength:120});const catLabel=el('label','Categoría');category=el('select');for(const [key,value]of categories.filter(x=>!['Material','Coche'].includes(x[0])&&(x[0]!=='Aviso oficial'||community.role==='Admin'))){const option=el('option',value);option.value=key;category.append(option);}category.value=saved.category||'General';catLabel.append(category);f.append(catLabel);body=field(f,'¿Qué quieres compartir?','textarea',saved.body||'',{required:true,maxLength:4000});
 f.oninput=()=>drafts.set('new-post:'+community.id,{title:title.value,category:category.value,body:body.value});f.finish();box.append(f,link('Volver al tablón','board','button ghost'));view.append(box);
}
function report(kind,id,c=community){const requestId=crypto.randomUUID();ask('Denunciar','El equipo de UrbaLink revisará el contenido señalado y tu explicación. En mensajes privados solo se enviará el mensaje seleccionado.','Enviar denuncia',async reason=>{const r=await api('/api/reports','POST',{requestId,communityId:c.id,kind,targetId:Number(id),reason});feedback.textContent='Denuncia enviada. Referencia #'+r.id;},true);}
function compose(target,key,submitLabel,action){
 const saved=drafts.get(key)||{body:'',requestId:crypto.randomUUID()};drafts.set(key,saved);let input;
 const f=form(submitLabel,async()=>{if(!input.value.trim())return;await action({body:input.value,requestId:saved.requestId});});
 input=field(f,target,'textarea',saved.body,{required:true,maxLength:2000});input.oninput=()=>{saved.body=input.value;saved.requestId=crypto.randomUUID();};f.finish();return f;
}
async function post(id){
 let data,isService=false;try{data=await api(root()+'/posts/'+id+'/comments');}catch(e){if(e.status!==404)throw e;data=await api(root()+'/services/'+id);isService=true;}
 const p=isService?data.service:data.post;heading(p.title);view.append(link('← Volver al tablón','board','button ghost'));const detail=card();detail.classList.add('reading');detail.append(el('span',label(p.category),'badge'),text(p.body,'body-text'),text(`${p.authorName} · ${date(p.createdAt)}`,'muted'));
 const actions=el('div',undefined,'actions');actions.append(button('Copiar enlace',async()=>{try{await navigator.clipboard.writeText(location.href);feedback.textContent='Enlace copiado.';}catch{feedback.textContent='Puedes compartir esta dirección: '+location.href;}},'ghost'),button('Denunciar publicación',async()=>report('Post',id),'ghost'));
 if(p.authorId===account.id || community.role==='Admin')actions.append(button('Retirar publicación',async()=>ask('Retirar publicación','Dejará de estar visible en el tablón.','Retirar',async()=>{await api(root()+'/posts/'+id,'DELETE');await go('board');}),'danger'));detail.append(actions);view.append(detail);
 if(isService){
  view.append(el('h2','Contacto privado'),text('Cada vecino tiene su propia conversación con quien ofrece el servicio.','muted'));
  if(p.authorId!==account.id)view.append(button('Contactar en privado',async()=>{const chat=await api(root()+'/services/'+id+'/conversations','POST');await go(`c/${community.id}/chat/Service/${id}/${chat.id}`);}));
  for(const chat of data.conversations){const n=card(p.authorId===account.id?chat.name:p.authorName,chat.lastMessage||'Conversación sin mensajes');n.append(link('Abrir conversación',`c/${community.id}/chat/Service/${id}/${chat.id}`));view.append(n);}return;
 }
 view.append(el('h2','Comentarios'));const list=el('div');view.append(list);
 const draw=items=>{for(const m of items){const n=card();n.append(text(m.body,'body-text'),text(`${m.authorName} · ${date(m.createdAt)}`,'muted'),button('Denunciar comentario',async()=>report('Comment',m.id),'ghost'));if(m.authorId===account.id||community.role==='Admin')n.append(button('Retirar comentario',async()=>ask('Retirar comentario','El comentario dejará de verse en esta publicación.','Retirar',async()=>{await api(root()+'/posts/'+id+'/comments/'+m.id,'DELETE');await post(id);}),'danger'));list.append(n);}};
 draw(data.items);if(!data.items.length)list.append(empty('Sé el primero en responder.'));let next=data.nextBefore;const more=button('Comentarios anteriores',async()=>{const page=await api(root()+'/posts/'+id+'/comments?before='+next);draw(page.items);next=page.nextBefore;more.hidden=next===null;},'secondary');more.hidden=next===null;view.append(more);
 const f=compose('Escribe un comentario','post:'+id,'Enviar comentario',async input=>{await api(root()+'/posts/'+id+'/comments','POST',input);drafts.delete('post:'+id);await post(id);});const composer=card();composer.classList.add('reading');composer.append(f);view.append(composer);
}
async function messages(){
 const data=await api('/api/conversations');heading('Tus mensajes','Conversaciones privadas de todas tus comunidades. Solo sus participantes pueden leerlas.');const list=el('div');view.append(list);
 const draw=items=>{for(const chat of items){const n=el('article',undefined,'message-card');n.append(el('h3',chat.otherName),text(chat.title),text(chat.lastMessage||'Todavía no hay mensajes.','preview'),text(chat.communityName,'muted'));if(chat.unreadCount)n.append(el('span',chat.unreadCount+' sin leer','badge'));n.append(link('Abrir conversación',`c/${chat.communityId}/chat/${chat.kind}/${chat.subjectId}/${chat.id}`,'button ghost'));list.append(n);}};draw(data.items);if(!data.items.length)list.append(empty('Aquí aparecerán tus conversaciones. Puedes contactar en privado desde una publicación de Servicios.'));
 let next=data.nextBefore;const more=button('Conversaciones anteriores',async()=>{const page=await api('/api/conversations?before='+next);draw(page.items);next=page.nextBefore;more.hidden=next===null;},'secondary');more.hidden=next===null;view.append(more,button('Actualizar mensajes',messages,'ghost'));
}
async function conversation(parts){
 const kind=parts[3],subject=parts[4],chatId=parts[5],segment={Object:'items',Service:'services',Animal:'animals'}[kind];
 if(!segment||!/^\d+$/.test(subject)||!/^\d+$/.test(chatId))throw new ApiError(400,'Enlace de conversación no válido.');
 const subjectPath=root()+'/'+segment+'/'+subject;const detail=await api(subjectPath),object=detail.item||detail.service||detail.notice,chat=detail.conversations.find(x=>String(x.id)===chatId);if(!chat)throw new ApiError(404,'La conversación no está disponible.');
 const path=subjectPath+'/conversations/'+chatId+'/messages',data=await api(path);const owner=object.ownerId||object.authorId,other=owner===account.id?chat.name:(object.ownerName||object.authorName);heading(other,object.title);view.append(link('← Todos los mensajes','messages','button ghost'),text('Conversación privada. Puedes denunciar mensajes concretos al equipo de UrbaLink.','muted'));
 const host=el('div',undefined,'conversation'),list=el('div',undefined,'chat-messages');let history=[...data.items];let next=data.nextBefore;
 const draw=()=>{list.replaceChildren();for(const m of history.sort((a,b)=>a.id-b.id)){const n=el('article',undefined,'bubble'+(m.authorId===account.id?' mine':''));n.append(text(m.body,'body-text'),text(`${m.name} · ${date(m.createdAt)}`,'muted'),button('Denunciar mensaje',async()=>report('Message',m.id),'ghost'));list.append(n);}};
 const more=button('Mensajes anteriores',async()=>{const page=await api(path+'?before='+next);history.push(...page.items);next=page.nextBefore;draw();more.hidden=next===null;},'secondary');more.hidden=next===null;host.append(more,list);draw();if(!history.length)list.append(empty('Escribe el primer mensaje.'));
 const f=compose('Tu mensaje','chat:'+chatId,'Enviar mensaje',async input=>{await api(path,'POST',input);drafts.delete('chat:'+chatId);await conversation(parts);});f.classList.add('chat-compose');host.append(f,button('Actualizar conversación',()=>conversation(parts),'ghost'));view.append(host);
 if(history.length && !document.hidden){try{await api(path+'/read','POST',{upToId:Math.max(...history.map(m=>m.id))});await refreshBell();}catch{feedback.textContent='Mensajes cargados. No se pudo actualizar el contador de lectura; pulsa Actualizar conversación.';}}
}
function join(){
 heading('Únete a tus vecinos','Introduce el código que te ha enviado el administrador de tu comunidad.');const box=card();box.classList.add('narrow');let code;const f=form('Unirme a la comunidad',async()=>{const joined=await api('/api/communities/join','POST',{code:code.value.trim()});community=joined;rememberCommunity();history.replaceState(null,'','#home');await hydrate();});code=field(f,'Código de invitación','text','',{required:true,minLength:32,maxLength:32,autocomplete:'off'});f.finish();box.append(f);view.append(box);
}
function createCommunity(){
 heading('Un espacio para tu comunidad','UrbaLink revisará tu solicitud antes de que puedas invitar vecinos y publicar.');const box=card();box.classList.add('narrow');let name;const f=form('Solicitar comunidad',async()=>{community=await api('/api/communities','POST',{name:name.value});rememberCommunity();history.replaceState(null,'','#home');await hydrate();});name=field(f,'Nombre de la comunidad','text','',{required:true,minLength:2,maxLength:100});f.finish();box.append(f);view.append(box);
}
function profile(){
 heading('Mi cuenta');const identity=card(account.name,account.email);identity.append(text('El mismo acceso funciona en la web y en la app.','muted'));view.append(identity);
 for(const c of communities){const n=card(c.name,c.role==='Admin'?'Administrador de comunidad':'Vecino');if(c.state!=='Active')n.append(text(c.state==='Pending'?'Pendiente de aprobación':'Comunidad suspendida','muted'));n.append(button('Abrir comunidad',async()=>{community=c;rememberCommunity();updateSelector();await go('home');},'secondary'));
 if(c.role==='Admin' && c.state==='Active')n.append(button('Crear invitación',async()=>{const invitation=await api(root(c)+'/invitations','POST');const label=el('label','Código de un solo uso · válido hasta '+date(invitation.expiresAt)),input=el('input');input.value=invitation.code;input.readOnly=true;input.onclick=()=>input.select();label.append(input);n.append(label);},'ghost'));
 if(c.state==='Active')n.append(button('Denunciar comunidad',async()=>report('Community',0,c),'ghost'));view.append(n);}
 const actions=el('div',undefined,'actions');actions.append(link('Unirme con una invitación','join'),link('Solicitar comunidad','create-community'),button('Cerrar sesión',async()=>ask('Cerrar sesión','Podrás volver a entrar con tu correo y contraseña.','Cerrar sesión',async()=>{await api('/api/auth/logout','POST');reset();history.replaceState(null,'','#home');login();}),'danger'));view.append(actions);
}
async function refreshBell(){if(!account||document.hidden)return;try{const n=await api('/api/notifications/count');$('unread').textContent=n.unreadCount>0?(n.unreadCount>99?'99+':String(n.unreadCount)):'';$('bell').setAttribute('aria-label','Mensajes privados: '+n.unreadCount+' sin leer');}catch{$('bell').setAttribute('aria-label','Mensajes privados. No se pudo actualizar el contador.');}}
setInterval(()=>{if(!busy)refreshBell();},10000);document.addEventListener('visibilitychange',()=>{if(!document.hidden&&!busy)refreshBell();});
async function boot(){try{account=await api('/api/me');}catch(e){if(e.status===401){login();return;}login();throw e;}await hydrate();}
$('start-retry').onclick=()=>run(boot);
await run(boot);
