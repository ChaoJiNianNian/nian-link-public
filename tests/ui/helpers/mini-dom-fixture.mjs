import fs from 'node:fs';
import vm from 'node:vm';
import {webcrypto} from 'node:crypto';

// The actual production script runs against a minimal DOM; all native replies are controlled by tests.
export function createMiniFixture() {
 const nodes=[];
 class Element {
  constructor(tag='div'){this.tagName=tag.toUpperCase();this.children=[];this.attributes={};this.dataset={};this.style={};this.handlers={};this.hidden=false;this._text='';nodes.push(this);}
  get className(){return this.attributes.class||'';} set className(value){this.attributes.class=value;}
  get textContent(){return this._text+this.children.map(x=>x.textContent||'').join('');} set textContent(value){this._text=String(value);this.children=[];}
  get src(){return this.attributes.src;}set src(value){this.attributes.src=value;}
  get parentElement(){return this.parent;}
  setAttribute(key,value){this.attributes[key]=String(value);if(key.startsWith('data-'))this.dataset[key.slice(5).replace(/-([a-z])/g,(_,x)=>x.toUpperCase())]=String(value);}
  getAttribute(key){return this.attributes[key]??null;}removeAttribute(key){delete this.attributes[key];}
  append(...values){for(const value of values){if(value&&typeof value==='object'){value.parent=this;this.children.push(value);}}}
  replaceChildren(...values){this.children=[];this._text='';this.append(...values);}
  remove(){if(this.parent)this.parent.children=this.parent.children.filter(x=>x!==this);}
  addEventListener(name,handler){(this.handlers[name]??=[]).push(handler);}
  async fire(name,event={}){for(const handler of this.handlers[name]||[])await handler({target:this,preventDefault(){},...event});}
  matches(selector){if(selector.startsWith('[')){const key=selector.slice(1,-1).split('=')[0];return Object.hasOwn(this.attributes,key);}if(selector.startsWith('.'))return this.className.split(/\s+/).includes(selector.slice(1));return this.tagName===selector.toUpperCase();}
  descendants(){return this.children.flatMap(x=>[x,...x.descendants()]);}
  querySelectorAll(selector){const parts=selector.trim().split(/\s+/);let candidates=this.descendants().filter(x=>x.matches(parts.at(-1)));if(parts.length>1)candidates=candidates.filter(x=>{let parent=x.parent;while(parent){if(parent.matches(parts[0]))return true;parent=parent.parent;}return false;});return candidates;}
  querySelector(selector){return this.querySelectorAll(selector)[0]||null;}
  closest(selector){return selector.split(',').some(x=>this.matches(x))?this:this.parent?.closest(selector)||null;}
  getBoundingClientRect(){return {width:374,height:524,left:0,top:0};}
 }
 const documentRoot=new Element('document'),stack=[documentRoot];
 const html=fs.readFileSync(new URL('../../../src/DesktopToolbox.Collaboration.Ui/index.html',import.meta.url),'utf8');
 for(const match of html.matchAll(/<\/?([\w-]+)([^>]*)>/g)) {
  const [raw,tag,attributes]=match;
  if(raw.startsWith('</')){if(stack.length>1)stack.pop();continue;}
  const element=new Element(tag);
  for(const attribute of attributes.matchAll(/([\w:-]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+)))?/g))element.setAttribute(attribute[1],attribute[2]??attribute[3]??attribute[4]??'');
  stack.at(-1).append(element);
  if(!['meta','link','input','img','br','hr'].includes(tag)&&!raw.endsWith('/>'))stack.push(element);
 }
 const root=nodes.find(x=>x.attributes.id==='nian-link-mini');
 const document={getElementById:id=>nodes.find(x=>x.attributes.id===id),createElement:tag=>new Element(tag),documentElement:nodes.find(x=>x.tagName==='HTML'),addEventListener(){}};
 const sent=[];const window={addEventListener(){},lucide:{createIcons(){}},chrome:{webview:{postMessage:message=>sent.push(message),postMessageWithAdditionalObjects:message=>sent.push(message),addEventListener(){}}}};
 let now=0;const intervals=[];
 vm.runInNewContext(fs.readFileSync(new URL('../../../src/DesktopToolbox.Collaboration.Ui/mini.js',import.meta.url),'utf8'),{window,document,crypto:webcrypto,TextDecoder,Uint8Array,atob,performance:{now:()=>now},setInterval:fn=>{intervals.push(fn);return intervals.length;},clearInterval(){},ResizeObserver:class{observe(){}},requestAnimationFrame(){},console});
 const reply=(request,result,ok=true)=>window.nianReceive({id:request.id,epoch:'test-epoch',ok,result,error:ok?undefined:String(result)});
 reply(sent.find(x=>x.action==='ready'),{epoch:'test-epoch'});
 let revision=0;
 const state=(patch={})=>window.nianState({epoch:'test-epoch',revision:++revision,platform:'Mac',theme:'dark',connected:true,keyboardSharingRunning:true,keyboardSharingConnected:true,keyboardCanStart:false,keyboardCanStop:true,canSwitchWindows:true,canSwitchMac:true,activeComputer:'Mac',previewEnabled:false,clipboardEnabled:false,sharedDirectory:'Win / 资料',transfers:[],...patch});
 state();
 return {root,window,sent,reply,state,advance:ms=>{now+=ms;for(const fn of intervals)fn();},q:selector=>root.querySelector(selector),find:(selector,text)=>root.querySelectorAll(selector).find(x=>x.textContent===text),last:action=>sent.filter(x=>x.action===action).at(-1),flush:async()=>{await Promise.resolve();await Promise.resolve();await Promise.resolve();}};
}
