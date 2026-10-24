'use strict';
const $=id=>document.getElementById(id),view=$('view'),feedback=$('feedback');
const categories=[['General','General'],['Ayuda','Necesito ayuda'],['Ofrezco','Servicios'],['Recomendación','Recomendaciones'],['Material','Compraventa'],['Coche','Trayectos'],['Aviso oficial','Aviso oficial']];
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
 const panel=card(register?'Crea tu cuenta':'Bienvenido a tu comunidad','Entra con tu correo electrónico y tu contraseña.');let name,email,password;
 const f=form(register?'Crear cuenta':'Iniciar sesión',async()=>{
  account=await api(register?'/api/web/register':'/api/web/session','POST',{...(register?{name:name.value}:{}),email:email.value,password:password.value});password.value='';drafts.clear();await hydrate();
 });
 if(register)name=field(f,'Tu nombre','text','',{required:true,minLength:1,maxLength:80,autocomplete:'name'});
 email=field(f,'Correo electrónico','email','',{required:true,maxLength:254,autocomplete:'username'});
 password=field(f,'Contraseña','password','',{required:true,minLength:1,maxLength:128,autocomplete:register?'new-password':'current-password'});
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
 if(!account.email){profile();feedback.textContent='Añade tu correo para volver a iniciar sesión. Conservas tu contraseña y todos tus datos.';return;}
 const route=currentRoute();lastRoute=route;const parts=route.split('?')[0].split('/');
 if(parts[0]==='c'){
  const selected=communities.find(c=>c.id===parts[1]);if(!selected)throw new ApiError(403,'No tienes acceso a esta comunidad. Comprueba tu cuenta o entra con una invitación.');community=selected;rememberCommunity();updateSelector();
 }
 const section=parts[0]==='c'?(parts[2]==='invitations'?'profile':parts[2]==='chat'?'messages':parts[2]==='items'?'market':parts[2]==='animals'?'animals':'board'):parts[0];document.querySelectorAll('[data-nav]').forEach(a=>{if(a.dataset.nav===section)a.setAttribute('aria-current','page');else a.removeAttribute('aria-current');});
 if(section==='messages' && parts[0]!=='c'){await messages();return;}
 if(section==='profile' && parts[0]!=='c'){profile();return;}
 if(section==='trash'){await communityTrash();return;}
 if(section==='join'){join();return;}
 if(section==='create-community'){createCommunity();return;}
 if(!community){welcome();return;}
 if(community.state!=='Active'){heading(community.name,community.state==='Pending'?'Tu comunidad está pendiente de aprobación por UrbaLink. Podrás invitar vecinos y publicar cuando se apruebe.':'Esta comunidad está suspendida por UrbaLink.');view.append(button('Actualizar estado',hydrate,'secondary'),link('Mi cuenta','profile','button ghost'));return;}
 if(parts[0]==='c' && parts[2]==='invitations'){await invitationsPage();return;}
 if(parts[0]==='c' && parts[2]==='post' && /^\d+$/.test(parts[3])){await post(parts[3]);return;}
 if(parts[0]==='c' && parts[2]==='chat'){await conversation(parts);return;}
 if(parts[0]==='c' && ['items','animals'].includes(parts[2]) && /^\d+$/.test(parts[3])){await catalogDetail(parts[2],parts[3]);return;}
 if(section==='market'||section==='animals'){await catalog(section==='market'?'items':'animals',new URLSearchParams(route.split('?')[1]||''));return;}
 if(section==='new-item'||section==='new-animal'){catalogForm(section==='new-item'?'items':'animals');return;}
 if(section==='board'){await board(new URLSearchParams(route.split('?')[1]||'').get('category'));return;}
 if(section==='rides'){await rides(new URLSearchParams(route.split('?')[1]||'').get('day'));return;}
 if(section==='new-ride'){rideForm();return;}
 if(section==='new-post'){newPost();return;}
 await home();
}
function welcome(){heading('Tu comunidad empieza aquí','Únete a tus vecinos con una invitación o solicita una nueva comunidad.');const actions=el('div',undefined,'actions');actions.append(link('Unirme con una invitación','join','button'),link('Solicitar comunidad','create-community'));view.append(actions);}
async function home(){
 const data=await api(root()+'/overview');heading('Hola, '+account.name);const hero=el('section',undefined,'welcome'),copy=el('div');copy.append(text('LA VIDA EN TU URBA','eyebrow'),el('h1','Un poco más cerca de tus vecinos.'),text(`${data.memberCount} vecinos · ${data.activePosts} publicaciones`));hero.append(copy,link('＋ Compartir algo','new-post'));view.append(hero);
 const grid=el('div',undefined,'grid');const boardCard=card('Lo que pasa cerca de ti','Preguntas, novedades y conversaciones de tu comunidad.');boardCard.append(link('Abrir el tablón','board','button ghost'));const help=card('Ayuda y servicios','Encuentra una mano, ofrece lo que sabes y comparte recomendaciones.');help.append(link('Explorar ayuda','board?category='+encodeURIComponent('Ayuda y servicios'),'button ghost'));const market=card('Compraventa',data.sharedItems+' objetos · venta, alquiler y subastas entre vecinos.');market.append(link('Explorar objetos','market','button ghost'));const animals=card('Animales','Avisos de perros y gatos perdidos o encontrados.');animals.append(link('Ver avisos','animals','button ghost'));const journeys=card('Trayectos',data.routes+' trayectos · comparte coche con tus vecinos.');journeys.append(link('Ir juntos','rides','button ghost'));grid.append(boardCard,help,market,journeys,animals);view.append(grid);if(data.notice){const notice=card('Aviso de tu comunidad: '+data.notice.title,data.notice.body);notice.append(link('Leer aviso','c/'+community.id+'/post/'+data.notice.id));view.append(notice);}
 const head=el('div',undefined,'section-head');head.append(el('h2','Últimas novedades'),link('Ver todas','board','button ghost'));view.append(head);
 if(!data.recent.length)view.append(empty('Todavía no hay publicaciones. Comparte la primera.'));for(const p of data.recent)view.append(postCard(p));view.append(button('Actualizar inicio',home,'ghost'));
}
function postCard(p){const n=el('article',undefined,'post');n.append(el('span',label(p.category),'badge'),el('h3'));n.querySelector('h3').append(link(p.title,`c/${p.communityId}/post/${p.id}`,''));n.append(text(p.body,'preview'),text(`${p.authorName} · ${date(p.createdAt)}`,'muted'),link(p.category==='Ofrezco'?'Ver servicio y contactar':'Ver publicación',`c/${p.communityId}/post/${p.id}`,'button ghost'));return n;}
async function board(category=null){
 if(category==='Material'){await go('market');return;}
 const query=category?'?category='+encodeURIComponent(category):'';const data=await api(root()+'/posts'+query);heading('Tablón de '+community.name,'Lo que compartimos hace comunidad.');view.append(link('＋ Nueva publicación','new-post','button'));
 const filters=el('div',undefined,'filters');for(const [key,title]of [['','Todas'],['Ayuda y servicios','Ayuda y servicios'],...categories])filters.append(link(title,key==='Material'?'market':'board'+(key?'?category='+encodeURIComponent(key):''),key===(category||'')?'button':'button secondary'));view.append(filters);
 const list=el('div');view.append(list);for(const p of data.items)list.append(postCard(p));if(!data.items.length)list.append(empty('No hay publicaciones en esta categoría.'));let next=data.nextBefore;
 const more=button('Ver publicaciones anteriores',async()=>{const page=await api(root()+'/posts'+query+(query?'&':'?')+'before='+next);for(const p of page.items)list.append(postCard(p));next=page.nextBefore;more.hidden=next===null;},'secondary');more.hidden=next===null;view.append(more);
}
function newPost(){
 heading('Comparte con tus vecinos','Las publicaciones y sus comentarios son visibles para tu comunidad. En Servicios, el contacto es privado.');const options=el('div',undefined,'actions');options.append(link('Publicar objeto','new-item'),link('Ofrecer trayecto','new-ride'),link('Aviso de animal','new-animal'));view.append(options);const box=card();box.classList.add('narrow');
 const saved=drafts.get('new-post:'+community.id)||{};let title,body,category;
 const f=form('Publicar',async()=>{const p=await api(root()+'/posts','POST',{title:title.value,body:body.value,category:category.value});drafts.delete('new-post:'+community.id);await go(`c/${community.id}/post/${p.id}`);});
 title=field(f,'Título','text',saved.title||'',{required:true,minLength:3,maxLength:120});const catLabel=el('label','Categoría');category=el('select');for(const [key,value]of categories.filter(x=>!['Material','Coche'].includes(x[0])&&(x[0]!=='Aviso oficial'||community.role==='Admin'))){const option=el('option',value);option.value=key;category.append(option);}category.value=category.children.length&&[...category.children].some(o=>o.value===saved.category)?saved.category:'General';catLabel.append(category);f.append(catLabel);body=field(f,'¿Qué quieres compartir?','textarea',saved.body||'',{required:true,maxLength:4000});
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
 if(p.authorId===account.id || community.role==='Admin')actions.append(button('Retirar publicación',async()=>ask('Retirar publicación','Dejará de estar visible en el tablón.','Retirar',async()=>{await api(root()+'/posts/'+id,'DELETE');await go('board');}),'danger'));actions.append(button('Actualizar publicación',()=>post(id),'ghost'));detail.append(actions);view.append(detail);
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
 const draw=items=>{for(const chat of items){const n=el('article',undefined,'message-card');n.append(el('h3',chat.otherName),text(chat.title),text(chat.lastMessage||'Todavía no hay mensajes.','preview'),text(chat.communityName,'muted'));if(chat.unreadCount)n.append(el('span',chat.unreadCount+' sin leer','badge'));n.append(link('Abrir conversación',`c/${chat.communityId}/chat/${chat.kind}/${chat.subjectId}/${chat.id}`,'button ghost'));list.append(n);}};draw(data.items);if(!data.items.length)list.append(empty('Aquí aparecerán tus conversaciones. Puedes contactar en privado desde Servicios, Compraventa o Animales.'));
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
 heading('Únete a tus vecinos','Introduce el código que te ha enviado el administrador de tu comunidad.');const box=card();box.classList.add('narrow');let code;const f=form('Unirme a la comunidad',async()=>{const joined=await api('/api/communities/join','POST',{code:code.value.trim()});community=joined;rememberCommunity();history.replaceState(null,'','#home');await hydrate();});code=field(f,'Código de invitación','text',new URLSearchParams(currentRoute().split('?')[1]||'').get('code')||'',{required:true,minLength:32,maxLength:32,autocomplete:'off'});f.finish();box.append(f);view.append(box);
}
function createCommunity(){
 heading('Un espacio para tu comunidad','UrbaLink revisará tu solicitud antes de que puedas invitar vecinos y publicar.');const box=card();box.classList.add('narrow');let name;const f=form('Solicitar comunidad',async()=>{community=await api('/api/communities','POST',{name:name.value});rememberCommunity();history.replaceState(null,'','#home');await hydrate();});name=field(f,'Nombre de la comunidad','text','',{required:true,minLength:2,maxLength:100});f.finish();box.append(f);view.append(box);
}
function profile(){
 heading('Mi cuenta');const identity=card(account.name,'Tu correo electrónico es el identificador para iniciar sesión.');let email;const edit=form('Guardar perfil',async()=>{account=await api('/api/me','PUT',{email:email.value});feedback.textContent='Perfil guardado.';});email=field(edit,'Correo electrónico','email',account.email||'',{required:true,maxLength:254,autocomplete:'email'});edit.finish();identity.append(edit);view.append(identity);
 for(const c of communities){const n=card(c.name,c.role==='Admin'?'Administrador de comunidad':'Vecino');if(c.state!=='Active')n.append(text(c.state==='Pending'?'Pendiente de aprobación':'Comunidad suspendida','muted'));n.append(button('Abrir comunidad',async()=>{community=c;rememberCommunity();updateSelector();await go('home');},'secondary'));
 if(c.role==='Admin' && c.state==='Active')n.append(link('Invitar vecinos',`c/${c.id}/invitations`,'button ghost'),button('Eliminar comunidad',async()=>deleteCommunity(c),'danger'));
 if(c.state==='Active')n.append(button('Denunciar comunidad',async()=>report('Community',0,c),'ghost'));view.append(n);}
 const actions=el('div',undefined,'actions');actions.append(link('Unirme con una invitación','join'),link('Solicitar comunidad','create-community'),link('Papelera de comunidades','trash'),button('Cerrar sesión',async()=>ask('Cerrar sesión','Podrás volver a entrar con tu correo y contraseña.','Cerrar sesión',async()=>{await api('/api/auth/logout','POST');reset();history.replaceState(null,'','#home');login();}),'danger'));view.append(actions);
}
async function refreshBell(){if(!account||document.hidden)return;try{const n=await api('/api/notifications/count');$('unread').textContent=n.unreadCount>0?(n.unreadCount>99?'99+':String(n.unreadCount)):'';$('bell').setAttribute('aria-label','Mensajes privados: '+n.unreadCount+' sin leer');}catch{$('bell').setAttribute('aria-label','Mensajes privados. No se pudo actualizar el contador.');}}
setInterval(()=>{if(!busy)refreshBell();},10000);document.addEventListener('visibilitychange',()=>{if(!document.hidden&&!busy)refreshBell();});
async function boot(){try{account=await api('/api/me');}catch(e){if(e.status===401){login();return;}login();throw e;}await hydrate();}
$('start-retry').onclick=()=>run(boot);
await run(boot);

function choice(f,title,values,value){const l=el('label',title),s=el('select');for(const [key,name]of values){const o=el('option',name);o.value=key;s.append(o);}if(value!==undefined)s.value=value;l.append(s);f.append(l);return s;}
function money(cents){return new Intl.NumberFormat('es-ES',{style:'currency',currency:'EUR'}).format(cents/100);}
function catalogPhoto(host,photo,title){if(!photo)return;const img=el('img');img.src='data:image/jpeg;base64,'+photo;img.alt=title;img.className='catalog-photo';host.append(img);}
function itemSummary(i){return ({Sale:'Venta',Rent:'Alquiler',Auction:'Subasta'}[i.mode]||i.mode)+' · '+money(i.highestBidCents??i.priceCents)+(i.period?' / '+({Hour:'hora',Day:'día',Week:'semana'}[i.period]):'')+' · '+({Active:i.isLent?'Prestado':'Disponible',Ended:'Subasta finalizada',Completed:'Completado',Withdrawn:'Retirado'}[i.state]||i.state);}
async function catalog(segment,query=new URLSearchParams()){
 const animal=segment==='animals',route=animal?'animals':'market';heading(animal?'Animales':'Compraventa',animal?'Ayuda a reunir perros y gatos perdidos con sus familias.':'Compra, vende, alquila y participa en subastas entre vecinos.');
 view.append(link(animal?'＋ Publicar aviso':'＋ Publicar objeto',animal?'new-animal':'new-item','button'));
 const filters=el('div',undefined,'filters'),options=animal?[['','Activos'],['closed=true','Cerrados']]:[['','Todos'],['mode=Sale','Venta'],['mode=Rent','Alquiler'],['mode=Auction','Subastas'],['scope=Mine','Mis objetos'],['scope=Activity','Mi actividad']];
 for(const [key,name]of options)filters.append(link(name,route+(key?'?'+key:''),query.toString()===key?'button':'button secondary'));view.append(filters);
 const path=root()+'/'+segment,suffix=query.toString(),data=await api(path+(suffix?'?'+suffix:'')),list=el('div',undefined,'grid');view.append(list);
 const draw=items=>{for(const i of items){const n=card(i.title,animal?i.statusLabel+' · '+i.location:itemSummary(i));catalogPhoto(n,i.photo,i.title);n.append(text(i.description,'preview'),link('Ver detalles',`c/${community.id}/${segment}/${i.id}`));list.append(n);}};draw(data.items);if(!data.items.length)list.append(empty(animal?'No hay avisos en esta lista.':'No hay objetos en esta lista.'));
 let next=data.nextBefore;const more=button('Ver anteriores',async()=>{const page=await api(path+'?'+(suffix?suffix+'&':'')+'before='+next);draw(page.items);next=page.nextBefore;more.hidden=next===null;},'secondary');more.hidden=next===null;view.append(more,button('Actualizar',()=>catalog(segment,query),'ghost'));
}
async function catalogDetail(segment,id){
 const animal=segment==='animals',path=root()+'/'+segment+'/'+id,data=await api(path),i=data.notice||data.item,owner=(i.authorId||i.ownerId)===account.id,active=(i.status||i.state)==='Active';
 heading(i.title,animal?i.statusLabel:itemSummary(i));view.append(link('← Volver',animal?'animals':'market','button ghost'));const box=card();catalogPhoto(box,i.photo,i.title);box.append(text(i.description,'body-text'),text(animal?`${i.authorName} · ${i.location} · ${date(i.occurredAt)}${i.isWithReporter?' · Está con quien publica el aviso':''}`:i.ownerName,'muted'));if(i.endsAt)box.append(text('Fin de subasta: '+date(i.endsAt)+(i.winnerName?' · Ganador: '+i.winnerName:'')));view.append(box);
 const kind=animal?'Animal':'Object',openChat=async interestedUserId=>{const chat=await api(path+'/conversations','POST',animal?{}:{interestedUserId:interestedUserId||null});await go(`c/${community.id}/chat/${kind}/${id}/${chat.id}`);};
 if(!owner && (active||(!animal&&i.winnerId===account.id)))box.append(button('Contactar en privado',()=>openChat()));
 if(!animal && owner && i.winnerId)box.append(button('Contactar con ganador',()=>openChat(i.winnerId)));
 for(const chat of data.conversations){const c=card(owner?chat.name:(i.authorName||i.ownerName),chat.lastMessage||'Sin mensajes');c.append(link('Abrir conversación',`c/${community.id}/chat/${kind}/${id}/${chat.id}`));view.append(c);}
 if(!animal && i.mode==='Auction' && active && !owner){let amount,bidRequestId=crypto.randomUUID();const f=form('Pujar',async()=>{await api(path+'/bids','POST',{requestId:bidRequestId,amountCents:Math.round(Number(amount.value)*100)});await catalogDetail(segment,id);});const minimum=i.highestBidCents===null?i.priceCents:i.highestBidCents+i.bidStepCents;amount=field(f,'Tu puja (€)','number',(minimum/100).toFixed(2),{required:true,min:Math.max(0.01,minimum/100),max:1000000,step:'0.01'});amount.oninput=()=>{bidRequestId=crypto.randomUUID();};f.finish();box.append(f,text(i.leading?'Tu puja va en cabeza.':`${i.bidCount} pujas`,'muted'));}
 if(animal && owner && active)box.append(button('Editar aviso',async()=>catalogForm(segment,i)));
 const confirmAction=(title,url,body)=>button(title,async()=>ask(title,'Confirma el cambio de este anuncio.',title,async()=>{await api(path+url,'POST',body);await catalogDetail(segment,id);}), 'secondary');
 if((active||(!animal&&i.state==='Ended'&&i.bidCount===0)) && (owner||community.role==='Admin'))box.append(confirmAction('Retirar anuncio',animal?'/close':'/withdraw',animal?{status:'Withdrawn'}:{}));
 if(animal && active && owner)box.append(confirmAction('Ha vuelto a casa','/close',{status:'Recovered'}));
 if(!animal && owner){if(active && i.mode==='Rent')box.append(confirmAction(i.isLent?'Marcar disponible':'Marcar prestado','/availability',{isLent:!i.isLent}));else if((active&&i.mode==='Sale')||(i.state==='Ended'&&i.bidCount>0))box.append(confirmAction('Marcar completado','/complete',{}));}
 box.append(button('Denunciar anuncio',async()=>report(animal?'Animal':'Item',id),'ghost'),button('Actualizar anuncio',()=>catalogDetail(segment,id),'ghost'));
}
async function jpegPhoto(file){
 if(!file)return null;if(!file.type.startsWith('image/'))throw new ApiError(400,'Selecciona una imagen.');
 const bitmap=await createImageBitmap(file);try{const canvas=document.createElement('canvas'),scale=Math.min(1,800/Math.max(bitmap.width,bitmap.height));canvas.width=Math.max(1,Math.round(bitmap.width*scale));canvas.height=Math.max(1,Math.round(bitmap.height*scale));const ctx=canvas.getContext('2d');ctx.fillStyle='white';ctx.fillRect(0,0,canvas.width,canvas.height);ctx.drawImage(bitmap,0,0,canvas.width,canvas.height);for(const quality of [.85,.65,.45,.25,.1]){const data=canvas.toDataURL('image/jpeg',quality).split(',')[1];if(atob(data).length<=40000)return data;}throw new ApiError(400,'La foto es demasiado detallada. Elige una imagen más pequeña.');}finally{bitmap.close();}
}
function catalogForm(segment,existing=null){
 const animal=segment==='animals',key=segment+':'+community.id+':'+(existing?.id||'new'),saved=drafts.get(key)||{requestId:crypto.randomUUID(),...(existing||{})};drafts.set(key,saved);heading(existing?'Editar aviso':animal?'Publicar aviso de animal':'Publicar objeto');const box=card();box.classList.add('narrow');let title,description,mode,price,period,ends,step,kind,species,name,zone,occurred,withReporter,photo,retainedPhoto=existing?.photo||null;
 const f=form(existing?'Guardar cambios':'Publicar',async()=>{let payload={requestId:saved.requestId,description:description.value,photo:photo.files.length?await jpegPhoto(photo.files[0]):retainedPhoto};
 if(animal)payload={...payload,kind:kind.value,species:species.value,name:name.value,location:zone.value,occurredAt:new Date(occurred.value).toISOString(),isWithReporter:kind.value==='Found'&&withReporter.checked};
 else payload={...payload,title:title.value,mode:mode.value,priceCents:Math.round(Number(price.value)*100),period:mode.value==='Rent'?period.value:null,endsAt:mode.value==='Auction'?new Date(ends.value).toISOString():null,bidStepCents:mode.value==='Auction'?Math.round(Number(step.value)*100):0};
 const result=await api(root()+'/'+segment+(existing?'/'+existing.id+'?version='+existing.version:''),existing?'PUT':'POST',payload);drafts.delete(key);await go(`c/${community.id}/${segment}/${existing?.id||result.id}`);});
 if(animal){kind=choice(f,'Aviso',[['Lost','Perdido'],['Found','Encontrado']],saved.kind);species=choice(f,'Animal',[['Dog','Perro'],['Cat','Gato']],saved.species);name=field(f,'Nombre (opcional)','text',saved.name||'',{maxLength:80});zone=field(f,'Zona aproximada','text',saved.location||'',{required:true,minLength:2,maxLength:160});const now=new Date();const localDate=v=>{const d=new Date(v);return new Date(d-d.getTimezoneOffset()*60000).toISOString().slice(0,16);};occurred=field(f,'Cuándo ocurrió','datetime-local',localDate(saved.occurredAt||now),{required:true,max:localDate(now)});withReporter=field(f,'Está conmigo','checkbox');withReporter.checked=saved.isWithReporter||false;const update=()=>{withReporter.disabled=kind.value!=='Found';if(withReporter.disabled)withReporter.checked=false;};kind.onchange=update;update();}
 else{title=field(f,'Título','text',saved.title||'',{required:true,minLength:3,maxLength:120});mode=choice(f,'Modalidad',[['Sale','Venta'],['Rent','Alquiler'],['Auction','Subasta']],saved.mode);price=field(f,'Precio o salida (€)','number',saved.price||'0',{required:true,min:0,max:1000000,step:'0.01'});period=choice(f,'Periodo de alquiler',[['Hour','Hora'],['Day','Día'],['Week','Semana']],saved.period);ends=field(f,'Fin de subasta','datetime-local',saved.ends||'');step=field(f,'Incremento mínimo de puja (€)','number',saved.step||'1',{min:.01,max:1000000,step:'0.01'});const update=()=>{period.closest('label').hidden=mode.value!=='Rent';ends.closest('label').hidden=step.closest('label').hidden=mode.value!=='Auction';ends.required=step.required=mode.value==='Auction';};mode.onchange=update;update();}
 description=field(f,'Descripción','textarea',saved.description||'',{required:true,maxLength:animal?2000:4000});photo=field(f,'Foto (opcional)','file','',{accept:'image/*'});const preview=el('div');catalogPhoto(preview,retainedPhoto,'Foto del anuncio');f.append(preview,button('Quitar foto',async()=>{photo.value='';retainedPhoto=null;preview.replaceChildren();saved.requestId=crypto.randomUUID();},'ghost'));photo.onchange=()=>run(async()=>{retainedPhoto=photo.files.length?await jpegPhoto(photo.files[0]):null;preview.replaceChildren();catalogPhoto(preview,retainedPhoto,'Foto del anuncio');});
 f.addEventListener('input',()=>{saved.requestId=crypto.randomUUID();Object.assign(saved,{description:description.value},animal?{kind:kind.value,species:species.value,name:name.value,location:zone.value,occurredAt:occurred.value,isWithReporter:withReporter.checked}:{title:title.value,mode:mode.value,price:price.value,period:period.value,ends:ends.value,step:step.value});});f.finish();box.append(f,link('Cancelar',animal?'animals':'market','button ghost'));view.append(box);
}

async function invitationsPage(generated=null){
 if(community.role!=='Admin')throw new ApiError(403,'Solo los administradores pueden gestionar invitaciones.');
 const path=root()+'/invitations',list=await api(path);heading('Invitar vecinos a '+community.name,'Comparte una invitación colectiva por tu grupo de vecinos. Caduca en siete días y cada vecino debe registrarse o entrar con su cuenta.');view.append(link('← Mi cuenta','profile','button ghost'));
 if(generated){const box=card('Invitación creada',`${generated.maxUses===1?'Un solo uso':'Hasta '+generated.maxUses+' nuevos vecinos'} · válida hasta ${date(generated.expiresAt)}`),url=location.origin+location.pathname+'#join?code='+encodeURIComponent(generated.code);const code=field(box,'Código','text',generated.code,{readOnly:true}),address=field(box,'Enlace para compartir','text',url,{readOnly:true});code.onclick=()=>code.select();address.onclick=()=>address.select();box.append(text('Copia el código o el enlace ahora; no se volverán a mostrar. Compártelo solo con vecinos de tu comunidad.','muted'),button('Copiar enlace',async()=>{try{await navigator.clipboard.writeText(url);feedback.textContent='Enlace copiado. Ya puedes pegarlo en tu grupo de vecinos.';}catch{feedback.textContent='Selecciona el enlace de arriba para copiarlo.';address.focus();address.select();}},'secondary'));view.append(box);}
 const box=card('Nueva invitación');let limit;const f=form('Crear invitación colectiva',async()=>{const invitation=await api(path+'?maxUses='+Number(limit.value),'POST');await invitationsPage(invitation);});limit=field(f,'Máximo de nuevos vecinos','number','100',{required:true,min:2,max:1000,step:1});f.finish();box.append(f,button('Crear invitación individual',async()=>{const invitation=await api(path,'POST');await invitationsPage(invitation);},'ghost'));view.append(box,el('h2','Invitaciones activas'));
 if(!list.length)view.append(empty('No hay invitaciones pendientes.'));for(const invitation of list){const n=card(invitation.maxUses===1?'Individual':'Colectiva',`${invitation.uses} de ${invitation.maxUses} usos · hasta ${date(invitation.expiresAt)}`);n.append(button('Revocar invitación',async()=>ask('Revocar invitación','El código y el enlace dejarán de admitir nuevos vecinos. Quienes ya se han unido conservarán su acceso.','Revocar',async()=>{await api(path+'/'+invitation.id,'DELETE');await invitationsPage();}),'danger'));view.append(n);}
}

function deleteCommunity(c){
 let name;ask('Eliminar comunidad','La comunidad irá a la papelera. Sus vecinos perderán el acceso y se invalidarán sus invitaciones. Escribe el nombre exacto para confirmar: '+c.name,'Eliminar',async()=>{await api(root(c)+'/delete','POST',{confirmName:name.value});community=null;history.replaceState(null,'','#profile');await hydrate();});name=field($('modal-fields'),'Nombre de la comunidad','text','',{required:true,maxLength:100});
}
async function communityTrash(){
 const communities=await api('/api/communities/trash');heading('Papelera de comunidades','Puedes restaurar las comunidades que administras. Las invitaciones anteriores seguirán invalidadas.');view.append(link('← Mi cuenta','profile','button ghost'));if(!communities.length)view.append(empty('No hay comunidades en la papelera.'));for(const c of communities){const n=card(c.name);n.append(button('Restaurar comunidad',async()=>ask('Restaurar comunidad','Los miembros volverán a acceder con su contenido. Tendrás que generar nuevas invitaciones.','Restaurar',async()=>{await api(root(c)+'/restore','POST');history.replaceState(null,'','#profile');await hydrate();})));view.append(n);}
}
function rideTime(minutes){return String(Math.floor(minutes/60)).padStart(2,'0')+':'+String(minutes%60).padStart(2,'0');}
function rideDays(mask){return ['Lun','Mar','Mié','Jue','Vie','Sáb','Dom'].filter((_,i)=>mask&(1<<i)).join(' · ');}
async function rides(day=null){
 if(day!==null&&!/^[0-6]$/.test(day))throw new ApiError(400,'Día de trayecto no válido.');const query=day===null?'':'day='+day,path=root()+'/rides',data=await api(path+(query?'?'+query:''));heading('Trayectos · Ir juntos','Comparte coche con tus vecinos. Las rutas se repiten cada semana en los días indicados.');view.append(link('＋ Ofrecer un trayecto','new-ride','button'));
 const filters=el('div',undefined,'filters');filters.append(link('Todos los días','rides',day===null?'button':'button secondary'));['Lun','Mar','Mié','Jue','Vie','Sáb','Dom'].forEach((name,i)=>filters.append(link(name,'rides?day='+i,String(i)===day?'button':'button secondary')));view.append(filters);const list=el('div',undefined,'grid');view.append(list);
 const draw=items=>{for(const r of items){const n=card(r.origin+' → '+r.destination,rideDays(r.days));n.append(text('Ida '+rideTime(r.departureMinutes)+(r.returnMinutes!==null?' · Vuelta '+rideTime(r.returnMinutes)+(r.returnNextDay?' (+1 día)':''):' · Solo ida')),text(`${r.seats} plazas · ${r.priceCents===0?'Gratis':money(r.priceCents)+' / asiento y sentido'}`),text(r.allowsDetour?'Acepta desvíos'+(r.detourDetails?': '+r.detourDetails:''):'Sin desvíos'));if(r.meetingPoint)n.append(text('Encuentro: '+r.meetingPoint));if(r.notes)n.append(text(r.notes,'body-text'));n.append(text('Conduce '+r.post.authorName,'muted'),link('Ver detalles y preguntas',`c/${community.id}/post/${r.post.id}`));if(r.post.authorId===account.id||community.role==='Admin')n.append(button('Retirar trayecto',async()=>ask('Retirar trayecto','Dejará de mostrarse en la comunidad.','Retirar',async()=>{await api(root()+'/posts/'+r.post.id,'DELETE');await rides(day);}),'danger'));list.append(n);}};draw(data.items);if(!data.items.length)list.append(empty('Todavía no hay trayectos para estos días.'));let next=data.nextBefore;const more=button('Ver trayectos anteriores',async()=>{const page=await api(path+'?'+(query?query+'&':'')+'before='+next);draw(page.items);next=page.nextBefore;more.hidden=next===null;},'secondary');more.hidden=next===null;view.append(more,link('Ver todas las publicaciones de coche','board?category=Coche','button ghost'),button('Actualizar trayectos',()=>rides(day),'ghost'));
}
function rideForm(){
 heading('Ofrecer un trayecto','Indica las horas locales de tu ruta y los días en los que se repite.');const key='ride:'+community.id,saved=drafts.get(key)||{requestId:crypto.randomUUID(),days:0};drafts.set(key,saved);const box=card();box.classList.add('narrow');let origin,destination,meeting,departure,hasReturn,back,nextDay,seats,price,detour,details,notes;const checks=[];
 const minutes=value=>{const [h,m]=value.split(':').map(Number);return h*60+m;};
 const f=form('Publicar trayecto',async()=>{const days=checks.reduce((mask,c,i)=>mask|(c.checked?1<<i:0),0);if(!days)throw new ApiError(400,'Selecciona al menos un día de la semana.');const r=await api(root()+'/rides','POST',{requestId:saved.requestId,origin:origin.value,destination:destination.value,days,departureMinutes:minutes(departure.value),returnMinutes:hasReturn.checked?minutes(back.value):null,returnNextDay:hasReturn.checked&&nextDay.checked,seats:Number(seats.value),priceCents:Math.round(Number(price.value)*100),allowsDetour:detour.checked,detourDetails:detour.checked?details.value:'',meetingPoint:meeting.value,notes:notes.value});drafts.delete(key);await go(`c/${community.id}/post/${r.post.id}`);});
 origin=field(f,'Origen','text',saved.origin||'',{required:true,minLength:2,maxLength:80});destination=field(f,'Destino','text',saved.destination||'',{required:true,minLength:2,maxLength:80});meeting=field(f,'Punto de encuentro (opcional)','text',saved.meeting||'',{maxLength:200});const week=el('fieldset');week.append(el('legend','Días de salida'));['Lun','Mar','Mié','Jue','Vie','Sáb','Dom'].forEach((name,i)=>{const c=field(week,name,'checkbox');c.checked=!!(saved.days&(1<<i));checks.push(c);});week.className='weekdays';f.append(week);
 departure=field(f,'Salida de ida','time',saved.departure||'08:00',{required:true});hasReturn=field(f,'También hago la vuelta','checkbox');hasReturn.checked=saved.hasReturn||false;back=field(f,'Salida de vuelta','time',saved.back||'18:00');nextDay=field(f,'Vuelvo al día siguiente','checkbox');nextDay.checked=saved.nextDay||false;const updateReturn=()=>{back.closest('label').hidden=nextDay.closest('label').hidden=!hasReturn.checked;back.required=hasReturn.checked;};hasReturn.onchange=updateReturn;updateReturn();
 seats=choice(f,'Plazas para pasajeros',Array.from({length:8},(_,i)=>[String(i+1),String(i+1)]),saved.seats||'1');price=field(f,'Precio por asiento y sentido (€)','number',saved.price||'0',{required:true,min:0,max:10000,step:'0.01'});f.append(text('0 € = gratis. El precio se aplica a cada sentido; el pago se acuerda entre vecinos.','muted'));detour=field(f,'Acepto hacer desvíos','checkbox');detour.checked=saved.detour||false;details=field(f,'Zonas o minutos de desvío','text',saved.details||'',{maxLength:300});const updateDetour=()=>details.closest('label').hidden=!detour.checked;detour.onchange=updateDetour;updateDetour();notes=field(f,'Observaciones (opcional)','textarea',saved.notes||'',{maxLength:1000});
 f.addEventListener('input',()=>{Object.assign(saved,{requestId:crypto.randomUUID(),origin:origin.value,destination:destination.value,meeting:meeting.value,days:checks.reduce((mask,c,i)=>mask|(c.checked?1<<i:0),0),departure:departure.value,hasReturn:hasReturn.checked,back:back.value,nextDay:nextDay.checked,seats:seats.value,price:price.value,detour:detour.checked,details:details.value,notes:notes.value});});f.finish();box.append(f,link('Cancelar','rides','button ghost'));view.append(box);
}
