(() => {
  'use strict';
  const root=document.getElementById('nian-link-mini'), q=s=>root.querySelector(s), all=s=>Array.from(root.querySelectorAll(s));
  const pending=new Map(), transferRows=new Map();
  let currentEpoch=null, revision=-1, snapshot=null, activePanel=null, sharedVersion=0, previewVersion=0, sharedListing=null;
  let previewIntent=null, refreshIntent=null, clipboardIntent=null, keyboardAction=null, dragDepth=0, resizeQueued=false, lastSize='';
  let lastFrameKey=null, lastFrameReceivedAt=0, blockedFrameKey=null, preparingTransfer=false, previousTransferIds=new Set();
  let deviceChanging=false, lastDevicesKey='', lastDeviceAck='', pendingDeviceAck='';
  const id=()=>Array.from(crypto.getRandomValues(new Uint8Array(16)),v=>v.toString(16).padStart(2,'0')).join('');
  const setText=(el,value)=>{const text=String(value??'');if(el&&el.textContent!==text)el.textContent=text;};
  const nativeAvailable=()=>Boolean(window.chrome?.webview||window.webkit?.messageHandlers?.nian);
  function call(action,payload={},objects=null) {
    if(deviceChanging&&action!=='device.select'&&!action.startsWith('window.')&&action!=='ready')return Promise.reject(new Error('正在切换设备，请稍候。'));
    const requestId=id(),message={id:requestId,action,payload};
    return new Promise((resolve,reject)=>{
      pending.set(requestId,{resolve,reject,epoch:currentEpoch,action});
      try {
        if(window.chrome?.webview) {
          if(objects?.length&&typeof window.chrome.webview.postMessageWithAdditionalObjects==='function')window.chrome.webview.postMessageWithAdditionalObjects(message,objects);
          else window.chrome.webview.postMessage(message);
        } else if(window.webkit?.messageHandlers?.nian)window.webkit.messageHandlers.nian.postMessage(message);
        else throw new Error('原生连接尚未就绪，请重新打开小窗。');
      } catch(error) {pending.delete(requestId);reject(error);}
    });
  }
  window.nianReceive=message=>{
    if(!message||typeof message.id!=='string')return;
    const entry=pending.get(message.id);if(!entry)return;
    if(message.epoch&&currentEpoch&&message.epoch!==currentEpoch)return;
    if(entry.action==='ready'&&message.result?.epoch)currentEpoch=message.result.epoch;
    pending.delete(message.id);
    if(message.ok)entry.resolve(message.result);else entry.reject(new Error(typeof message.error==='string'?message.error:message.error?.message||'操作未完成，请重试。'));
  };
  function abortPending(message) {for(const entry of pending.values())entry.reject(new Error(message));pending.clear();}
  window.addEventListener('pagehide',()=>abortPending('小窗已重新加载，未完成操作已取消。'));
  const run=async(action,payload={},objects=null)=>{try{return await call(action,payload,objects);}catch(error){toast(error.message);return null;}};
  function toast(text) {setText(q('[data-toast]'),text);q('[data-toast]').hidden=!text;measure();}
  function statusText(frame) {
    if(frame?.error)return frame.error;
    return ({off:'对方已关闭画面，请在对方开启', 'local-off':'画面已关闭',locked:'对方已锁屏，解锁后继续',permission:'请在对方电脑允许屏幕录制',offline:'对方已断开连接',disconnected:'对方已断开连接',paused:'画面已暂停','display-unavailable':'对方显示器暂不可用'})[frame?.state]||'正在等待对方画面…';
  }
  function clearFrame() {const image=q('[data-frame-image]');image.removeAttribute('src');image.hidden=true;q('[data-framebadge]').hidden=true;}
  const frameKey=frame=>frame?.capturedAtUtc||frame?.jpegBytes||null;
  const refreshSeconds=()=>[1,5,10,30,60].includes(refreshIntent??snapshot?.previewRefreshSeconds)?refreshIntent??snapshot.previewRefreshSeconds:30;
  function renderPreview() {
    if(!snapshot)return;
    const enabled=previewIntent??snapshot.previewEnabled;
    q('[data-preview-toggle]').checked=Boolean(enabled);q('[data-preview-wrap]').hidden=!enabled;setText(q('[data-live-state]'),enabled?'已开启':'已关闭');
    q('[data-preview-refresh]').value=String(refreshSeconds());
    const frame=snapshot.preview||{},bytes=frame.jpegBytes;
    const key=frameKey(frame),candidate=enabled&&frame.state==='live'&&typeof bytes==='string'&&bytes.length<=1398104&&bytes.startsWith('/9j/');
    if(candidate&&key!==lastFrameKey){lastFrameKey=key;lastFrameReceivedAt=performance.now();}
    const stale=candidate&&performance.now()-lastFrameReceivedAt>(refreshSeconds()*1000+7000);
    const live=candidate&&!stale&&key!==blockedFrameKey;
    if(live) {
      const image=q('[data-frame-image]'),url='data:image/jpeg;base64,'+bytes;
      if(image.getAttribute('src')!==url)image.src=url;image.hidden=false;q('[data-framebadge]').hidden=false;
    } else clearFrame();
    q('[data-frame-status]').hidden=live;setText(q('[data-frame-status]'),stale?'画面暂未更新，正在等待新画面…':statusText(frame));setText(q('[data-frame-state]'),live?refreshSeconds()+' 秒刷新':stale?'画面暂未更新':enabled?'等待画面':'画面已关闭');
    measure();
  }
  function renderKeyboard() {
    if(!snapshot)return;
    const running=snapshot.keyboardSharingRunning===true,connected=snapshot.keyboardSharingConnected===true,busy=keyboardAction!==null,cleanup=!running&&snapshot.keyboardCanStop===true;
    const message=keyboardAction==='start'?'正在启动共享…':keyboardAction==='stop'?'正在停止键鼠共享…':cleanup?'需要完成停止':snapshot.keyboardSharingMessage||(connected?'键鼠已连接':running?'键鼠已启动，等待连接':snapshot.keyboardSharingRunning===false?'键鼠共享未启动':'正在确认键鼠状态');
    setText(q('[data-connected]'),message);q('[data-connected]').title=message;
    q('.nl-sub .nl-dot').style.background=connected?'var(--nl-green)':'var(--nl-muted)';
    const start=q('[data-start-sharing]');start.hidden=connected||running||cleanup;start.disabled=busy||snapshot.keyboardCanStart!==true;setText(start,keyboardAction==='start'?'启动中…':'启动共享');
    const settingsStart=q('[data-settings-start-sharing]'),settingsStop=q('[data-settings-stop-sharing]');
    settingsStart.hidden=running||cleanup;settingsStart.disabled=busy||snapshot.keyboardCanStart!==true;
    settingsStop.hidden=!running&&snapshot.keyboardCanStop!==true;settingsStop.disabled=busy||snapshot.keyboardCanStop!==true;setText(settingsStop,cleanup?'完成停止':'停止键鼠共享');
    setText(q('[data-keyboard-settings-status]'),message);setText(q('[data-keyboard-data-status]'),'文件与画面：'+(snapshot.connected===true?'已连接':'未连接'));
    for(const button of all('[data-switch]')) {
      const platform=button.dataset.switch,allowed=snapshot[platform==='Windows'?'canSwitchWindows':'canSwitchMac']===true;
      const multi=Array.isArray(snapshot.devices)&&snapshot.devices.length>0;
      const local=platform===(snapshot.platform==='Mac'?'Mac':'Windows');
      const label=multi?(local?'本机':selectedDeviceLabel(snapshot)):platform;
      const active=multi?snapshot.activeComputer===(local?snapshot.localDeviceName:snapshot.selectedDevice):snapshot.activeComputer===platform;
      button.disabled=busy||!allowed;button.title=allowed?'':snapshot.keyboardSharingMessage||'请先在两台电脑启动键鼠共享';
      button.setAttribute('aria-pressed',String(active));setText(button.querySelector('span'),active?label+' · 使用中':'切到 '+label);
    }
  }
  async function changeKeyboard(action) {
    if(keyboardAction||snapshot?.[action==='start'?'keyboardCanStart':'keyboardCanStop']!==true)return;
    keyboardAction=action;renderKeyboard();
    try {await call('connection.'+action);toast('');}
    catch(error){toast(error.message);}
    finally {keyboardAction=null;renderKeyboard();measure();}
  }
  function renderTransfers(transfers) {
    const visible=Array.isArray(transfers)?transfers.slice(-5).reverse():[],keep=new Set();
    for(const item of visible) {
      if(typeof item.id!=='string')continue;keep.add(item.id);
      let row=transferRows.get(item.id);
      if(!row) {
        const element=document.createElement('div');element.className='nl-transfer';
        const line=document.createElement('div');line.className='nl-transferline';const name=document.createElement('span'),percent=document.createElement('span');line.append(name,percent);
        const track=document.createElement('div');track.className='nl-transfertrack';const fill=document.createElement('div');fill.className='nl-transferfill';track.append(fill);
        const state=document.createElement('div');state.className='nl-filemeta';element.append(line,track,state);row={element,name,percent,fill,state};transferRows.set(item.id,row);q('[data-transfers]').append(element);
      }
      const percent=item.totalBytes>0?Math.min(100,Math.max(0,Math.round(item.bytesTransferred/item.totalBytes*100))):item.state==='complete'?100:0;
      setText(row.name,item.name);setText(row.percent,percent+'%');row.fill.style.width=percent+'%';setText(row.state,transferText(item));
    }
    for(const [key,row] of transferRows)if(!keep.has(key)){row.element.remove();transferRows.delete(key);}
  }
  function bytes(value) {return value>=1073741824?(value/1073741824).toFixed(1)+' GB':value>=1048576?(value/1048576).toFixed(1)+' MB':value>=1024?(value/1024).toFixed(1)+' KB':(value||0)+' B';}
  function selectedDeviceLabel(state) {const item=state.devices?.find(device=>device.name===state.selectedDevice);return typeof item?.label==='string'?item.label.slice(0,64):'对方设备';}
  function renderDeviceChoice(state) {
    const devices=Array.isArray(state.devices)?state.devices.filter(device=>device&&typeof device.name==='string'&&device.name.length>0&&device.name.length<=64).slice(0,4):[];
    const container=q('[data-device-chooser]'),picker=q('[data-device-picker]');container.hidden=devices.length===0;
    const key=JSON.stringify(devices.map(device=>[device.name,typeof device.label==='string'?device.label.slice(0,64):device.name]));
    if(key!==lastDevicesKey){lastDevicesKey=key;const options=devices.map(device=>{const option=document.createElement('option');option.value=device.name;option.textContent=typeof device.label==='string'?device.label.slice(0,64):device.name;return option;});picker.replaceChildren(...options);}
    if(!deviceChanging)picker.value=devices.some(device=>device.name===state.selectedDevice)?state.selectedDevice:'';
  }
  function acknowledgeDevice() {
    if(deviceChanging||snapshot?.platform!=='Windows'||snapshot?.multiDeviceMode!==true||!Number.isSafeInteger(snapshot.deviceVersion)||
      !snapshot.devices?.some(device=>device.name===snapshot.selectedDevice)||q('[data-device-picker]').value!==snapshot.selectedDevice)return;
    const name=snapshot.selectedDevice,version=snapshot.deviceVersion,key=name+':'+version;
    if(key===lastDeviceAck||key===pendingDeviceAck)return;pendingDeviceAck=key;
    void call('device.rendered',{name,version}).then(()=>{if(snapshot?.selectedDevice===name&&snapshot?.deviceVersion===version)lastDeviceAck=key;})
      .catch(()=>{}).finally(()=>{if(pendingDeviceAck===key)pendingDeviceAck='';});
  }
  function transferText(item) {const queued=item.state==='queued'&&snapshot?.platform==='Windows'?'等待 Mac 接收，请确认 Mac 已连接并设置收件文件夹':null;const label=queued||({complete:'已完成',completed:'已完成',queued:'等待传输',preparing:'准备中',sending:'发送中',receiving:'接收中',paused:'已暂停',reconnecting:'正在重连',failed:'传输未完成',cancelled:'已取消'})[item.state]||item.state;return label+' · '+bytes(item.bytesTransferred)+' / '+bytes(item.totalBytes)+(item.error?' · '+item.error:'');}
  window.nianState=next=>{
    if(!next||typeof next!=='object')return;
    if(next.epoch&&currentEpoch&&next.epoch!==currentEpoch)return;
    if(next.epoch&&!currentEpoch)currentEpoch=next.epoch;
    if(Number.isFinite(next.revision)&&next.revision<revision)return;
    revision=Number.isFinite(next.revision)?next.revision:revision+1;
    const previousShare=snapshot?.sharedDirectory,previousDevice=snapshot?.selectedDevice;snapshot=next;
    const macCandidate=next.platform==='Mac'&&next.multiDeviceMode===true;
    root.dataset.macCandidateBlue=String(macCandidate);
    q('[data-preview-toggle]').setAttribute('aria-label',macCandidate?'查看对方画面':'实时画面');
    if(previousDevice&&previousDevice!==next.selectedDevice){clearFrame();lastFrameKey=null;lastFrameReceivedAt=0;invalidateShared();}
    if(previewIntent!==null&&Boolean(next.previewEnabled)===previewIntent)previewIntent=null;
    if(refreshIntent!==null&&next.previewRefreshSeconds===refreshIntent)refreshIntent=null;
    if(clipboardIntent!==null&&Boolean(next.clipboardEnabled)===clipboardIntent)clipboardIntent=null;
    const remote=Array.isArray(next.devices)&&next.devices.length?selectedDeviceLabel(next):(next.platform==='Mac'?'Windows':'Mac');
    document.documentElement.style.colorScheme=next.theme==='dark'?'dark':next.theme==='light'?'light':'light dark';
    renderDeviceChoice(next);renderKeyboard();setText(q('[data-live-title]'),remote+' 实时画面');setText(q('[data-framebadge]'),remote+' · 桌面');
    q('[data-screen]').dataset.remote=remote;
    q('[data-pin]').setAttribute('aria-pressed',String(Boolean(next.pinned)));q('.nl-drag').style.cursor=next.pinned?'default':'grab';
    q('.nl-window').hidden=Boolean(next.collapsed);q('.nl-capsule').hidden=!next.collapsed;root.style.width=next.collapsed?'59px':'374px';
    const clipboard=clipboardIntent??next.clipboardEnabled;q('[data-clipboard-toggle]').checked=Boolean(clipboard);setText(q('[data-clip-caption]'),clipboard?'剪贴板 · 开':'剪贴板 · 关');q('[data-clip-dot]').hidden=!clipboard;setText(q('[data-clipboard-status]'),next.clipboardMessage||'剪贴板互通已暂停。');setText(q('[data-clipboard-privacy]'),'开启后，新复制的文字和链接可能发送到另一台电脑；单次最多 256 KB，超出请分段复制。');
    setText(q('[data-transfer-title]'),'发送到 '+remote);setText(q('[data-drag-label]'),'松开发送到 '+remote);setText(q('[data-receive-path]'),next.receiveDirectory?'本机收件：'+next.receiveDirectory:'本机收件目录未设置');setText(q('[data-share-path]'),next.sharedDirectory||(next.sharedFolderIsRemote===true?'来自主控 · 在线查看':next.platform==='Mac'?'来自 Windows · 在线查看':'尚未选择共享目录'));
    setText(q('[data-receive-setting]'),next.receiveDirectory||'尚未选择');setText(q('[data-share-setting]'),next.sharedDirectory||'尚未选择');q('[data-stop-share]').hidden=!next.sharedDirectory;
    q('[data-choose-receive-inline]').hidden=Boolean(next.receiveDirectory);
    const receiveError=typeof next.receiveError==='string'&&next.receiveError.length>0?next.receiveError:null;
    q('[data-receive-needed]').hidden=!(next.platform==='Mac'&&(next.receiveSetupRequired===true&&!next.receiveDirectory||receiveError));
    setText(q('[data-receive-needed-text]'),receiveError||'有文件等待接收，请先选择 Mac 收件文件夹。');
    const note=next.sharedFolderIsRemote===true?'在线查看主控共享的资料；保存到本机后可带走。':next.platform==='Windows'?'此目录供对方查看；另存会复制到本机收件目录。':'在线查看 Windows 资料；保存到本机后可带走。';setText(q('[data-shared-notice]'),note);
    const maxHeight=Number.isFinite(next.maxHeight)?next.maxHeight:1000;const receiveReserve=q('[data-receive-needed]').hidden?0:(Number(q('[data-receive-needed]').offsetHeight)||0)+12;for(const panel of all('[data-detail]'))panel.style.maxHeight=Math.max(100,Math.min(330,maxHeight-(next.previewEnabled?524:286)-36-receiveReserve))+'px';
    const mac=next.platform==='Mac';
    for(const element of all('[data-windows-setting]'))element.hidden=mac;
    for(const element of all('[data-local-share-setting]'))element.hidden=mac||next.sharedFolderIsRemote===true;
    q('[data-stop-share]').hidden=mac||!next.sharedDirectory;
    q('[data-mac-settings]').hidden=!mac;
    q('[data-pause-send]').hidden=!next.canPauseSending;
    q('[data-retry-send]').hidden=!next.canRetrySending;
    const wireless=next.wireless||{};q('[data-wireless-toggle]').checked=Boolean(wireless.enabled);q('[data-wireless-toggle]').disabled=!wireless.canChange;
    setText(q('[data-wireless-state]'),wireless.error||wireless.status||(wireless.enabled?'已开启':'已关闭'));
    q('[data-wireless-retry]').hidden=!wireless.needsRestoreRetry;q('[data-wireless-retry]').disabled=!wireless.canRetryRestore;
    renderTransfers(next.transfers);renderPreview();acknowledgeDevice();
    if(preparingTransfer&&(next.transfers||[]).some(item=>!previousTransferIds.has(item.id)||['sending','receiving'].includes(item.state))){preparingTransfer=false;toast('');}
    if(previousShare!==undefined&&previousShare!==next.sharedDirectory){invalidateShared();if(activePanel==='shared')loadShared();}
    measure();
  };
  function setPanel(name) {
    activePanel=activePanel===name?null:name;
    for(const panel of all('[data-detail]'))panel.hidden=panel.dataset.detail!==activePanel;
    for(const button of all('[data-panel]'))button.setAttribute('aria-expanded',String(button.dataset.panel===activePanel));
    previewVersion++;clearFilePreview();
    if(activePanel==='shared')loadShared(sharedListing?.rootId??null,sharedListing?.relativePath||'');
    else sharedVersion++;
    measure();
  }
  function clearFilePreview(){const panel=q('[data-file-preview]');panel.replaceChildren();panel.hidden=true;}
  function invalidateShared(){sharedVersion++;previewVersion++;sharedListing=null;q('[data-shared-list]').replaceChildren();clearFilePreview();}
  function icon(name) {const node=document.createElement('i');node.dataset.lucide=name;return node;}
  async function loadShared(rootId=null,relativePath='') {
    const version=++sharedVersion;previewVersion++;clearFilePreview();
    let listing;
    try{listing=await call('shared.list',{rootId,relativePath});}
    catch(error){if(version===sharedVersion&&activePanel==='shared'){sharedListing=null;q('[data-shared-list]').replaceChildren();toast(error.message);}return;}
    if(!listing||version!==sharedVersion||activePanel!=='shared')return;sharedListing=listing;
    const container=q('[data-shared-list]');container.replaceChildren();
    if(listing.rootId) {const back=document.createElement('button');back.className='nl-miniaction';back.textContent='返回上一级';back.onclick=()=>{const path=listing.relativePath||'',slash=path.lastIndexOf('/');loadShared(path?listing.rootId:null,path?slash<0?'':path.slice(0,slash):'');};container.append(back);}
    if(!listing.items?.length) {const empty=document.createElement('p');empty.className='nl-notice';empty.textContent='这个目录里还没有资料。';container.append(empty);}
    for(const item of listing.items||[]) {
      const row=document.createElement('div');row.className='nl-file';const badge=document.createElement('span');badge.className='nl-fileicon';badge.append(icon(item.isDirectory?'folder-open':'file-text'));
      const info=document.createElement('div');info.className='nl-fileinfo';const name=document.createElement('div');name.className='nl-filename';name.textContent=item.name;name.title=item.name;const meta=document.createElement('div');meta.className='nl-filemeta';meta.textContent=item.isDirectory?'文件夹':bytes(item.length);info.append(name,meta);
      const open=document.createElement('button');open.className='nl-miniaction cursor-interaction';open.textContent=item.isDirectory?'打开':'预览';open.onclick=()=>item.isDirectory?loadShared(item.rootId,item.relativePath):previewFile(item);
      const save=document.createElement('button');save.className='nl-miniaction cursor-interaction';save.textContent=snapshot?.platform==='Windows'?'另存':'保存到本机';save.onclick=async()=>{const result=await run('shared.download',{rootId:item.rootId,relativePath:item.relativePath});if(result)toast(snapshot?.platform==='Windows'?'已另存到本机收件目录。':'已保存到本机，可离线使用。');};row.append(badge,info,open,save);container.append(row);
    }
    window.lucide?.createIcons({attrs:{width:16,height:16}});measure();
  }
  async function previewFile(item) {
    const version=++previewVersion,folderVersion=sharedVersion;clearFilePreview();let value;
    try{value=await call('shared.preview',{rootId:item.rootId,relativePath:item.relativePath});}
    catch(error){if(version===previewVersion&&activePanel==='shared')toast(error.message);return;}
    if(!value||version!==previewVersion||folderVersion!==sharedVersion||activePanel!=='shared')return;
    const panel=q('[data-file-preview]');panel.replaceChildren();const title=document.createElement('div');title.textContent=value.name;panel.append(title);
    if(['image/jpeg','image/png','image/gif','image/webp','image/bmp'].includes(value.contentType)) {const image=document.createElement('img');image.className='nl-file-preview-image';image.alt=value.name;image.src='data:'+value.contentType+';base64,'+value.bytes;panel.append(image);}
    else if(value.contentType?.startsWith('text/')||value.contentType==='application/json') {const text=document.createElement('pre');text.style.whiteSpace='pre-wrap';text.style.overflowWrap='anywhere';try{text.textContent=new TextDecoder('utf-8').decode(Uint8Array.from(atob(value.bytes),c=>c.charCodeAt(0)));}catch{text.textContent='文本暂不可用，请保存后打开。';}panel.append(text);}
    else {const text=document.createElement('p');text.textContent='此格式请先保存到本机，再用对应软件打开。';panel.append(text);}
    panel.hidden=false;measure();
  }
  function buildSettings() {
    const container=q('[data-settings-body]');
    const keyboard=document.createElement('div');keyboard.className='nl-keyboard-settings';
    const keyboardRow=document.createElement('div');keyboardRow.className='nl-settings-row';const label=document.createElement('span');label.textContent='键鼠共享';keyboardRow.append(label);
    for(const [attribute,action,title] of [['data-settings-start-sharing','start','启动共享'],['data-settings-stop-sharing','stop','停止键鼠共享']]) {const button=document.createElement('button');button.className='nl-secondary';button.setAttribute(attribute,'');button.textContent=title;button.disabled=true;button.addEventListener('click',()=>changeKeyboard(action));keyboardRow.append(button);}keyboard.append(keyboardRow);
    const keyboardStatus=document.createElement('div');keyboardStatus.className='nl-notice';keyboardStatus.setAttribute('data-keyboard-settings-status','');keyboard.append(keyboardStatus);
    const dataStatus=document.createElement('div');dataStatus.className='nl-notice';dataStatus.setAttribute('data-keyboard-data-status','');keyboard.append(dataStatus);container.append(keyboard);
    const refreshRow=document.createElement('div');refreshRow.className='nl-settings-row';const refreshLabel=document.createElement('label');refreshLabel.textContent='画面刷新';refreshLabel.setAttribute('for','nl-preview-refresh');
    const refresh=document.createElement('select');refresh.id='nl-preview-refresh';refresh.setAttribute('data-preview-refresh','');refresh.setAttribute('aria-label','画面刷新间隔');
    for(const seconds of [1,5,10,30,60]){const option=document.createElement('option');option.value=String(seconds);option.textContent=seconds+' 秒'+(seconds===30?'（默认）':'');refresh.append(option);}refresh.value='30';
    refresh.addEventListener('change',async()=>{const seconds=Number(refresh.value);if(![1,5,10,30,60].includes(seconds)){refresh.value=String(refreshSeconds());return;}refreshIntent=seconds;refresh.disabled=true;renderPreview();try{await call('preview.setRefreshSeconds',{seconds});}catch(error){if(refreshIntent===seconds)refreshIntent=null;renderPreview();toast(error.message);}finally{refresh.disabled=false;}});
    refreshRow.append(refreshLabel,refresh);container.append(refreshRow);
    const refreshNote=document.createElement('p');refreshNote.className='nl-notice';refreshNote.setAttribute('data-preview-refresh-note','');refreshNote.textContent='采集 Windows 画面时，鼠标指针可能闪一下。';container.append(refreshNote);
    for(const [kind,title] of [['receive','本机收件目录'],['shared','Windows 共享目录']]) {
      const row=document.createElement('div');row.className='nl-settings-row';const label=document.createElement('span');label.textContent=title;const choose=document.createElement('button');choose.className='nl-secondary';choose.textContent='选择文件夹';choose.onclick=()=>run('directory.choose',{kind});row.append(label,choose);container.append(row);
      const path=document.createElement('div');path.className='nl-settings-path';path.setAttribute(kind==='receive'?'data-receive-setting':'data-share-setting','');container.append(path);
      if(kind==='shared'){row.setAttribute('data-windows-setting','');path.setAttribute('data-windows-setting','');row.setAttribute('data-local-share-setting','');path.setAttribute('data-local-share-setting','');}
    }
    const actions=document.createElement('div');actions.className='nl-actions-row';
    for(const [label,attribute,action] of [['打开本机收件','data-open-inbox','inbox.open'],['停止共享此文件夹','data-stop-share','shared.stop']]) {const button=document.createElement('button');button.className='nl-miniaction';button.textContent=label;button.setAttribute(attribute,'');button.onclick=()=>run(action);actions.append(button);}container.append(actions);
    const notice=document.createElement('p');notice.className='nl-notice';notice.style.marginTop='10px';notice.textContent='画面开关独立记忆，切换电脑不会自动开启。';container.append(notice);
    const windowsConnection=document.createElement('button');windowsConnection.className='nl-miniaction';windowsConnection.textContent='连接与显示器设置';windowsConnection.setAttribute('data-windows-setting','');windowsConnection.setAttribute('data-open-windows-settings','');windowsConnection.hidden=true;windowsConnection.onclick=()=>run('connection.openSettings');container.append(windowsConnection);
    const mac=document.createElement('div');mac.setAttribute('data-mac-settings','');mac.hidden=true;
    const connection=document.createElement('button');connection.className='nl-miniaction';connection.textContent='连接与权限设置';connection.setAttribute('data-open-mac-settings','');connection.onclick=()=>run('connection.openSettings');mac.append(connection);
    const row=document.createElement('div');row.className='nl-settings-row';const title=document.createElement('span');title.textContent='无线顺畅模式';
    const toggle=document.createElement('input');toggle.type='checkbox';toggle.className='nl-toggle';toggle.setAttribute('role','switch');toggle.setAttribute('aria-label','无线顺畅模式');toggle.setAttribute('data-wireless-toggle','');
    toggle.addEventListener('change',async()=>{toggle.disabled=true;await run('wireless.setEnabled',{enabled:toggle.checked});toggle.checked=Boolean(snapshot?.wireless?.enabled);toggle.disabled=!snapshot?.wireless?.canChange;});row.append(title,toggle);mac.append(row);
    const state=document.createElement('div');state.className='nl-notice';state.setAttribute('data-wireless-state','');mac.append(state);
    const description=document.createElement('p');description.className='nl-notice';description.textContent='鼠标卡顿时开启，可能影响隔空投送和无线投屏。';mac.append(description);
    const retry=document.createElement('button');retry.className='nl-miniaction';retry.textContent='重试恢复';retry.setAttribute('data-wireless-retry','');retry.onclick=()=>run('wireless.retry');mac.append(retry);container.append(mac);
    const transferActions=document.createElement('div');transferActions.className='nl-actions-row';
    for(const [label,attribute,action] of [['暂停发送','data-pause-send','files.pause'],['继续上次发送','data-retry-send','files.retry']]){const button=document.createElement('button');button.className='nl-miniaction';button.textContent=label;button.setAttribute(attribute,'');button.hidden=true;button.onclick=()=>run(action);transferActions.append(button);}q('[data-transfers]').parentElement.append(transferActions);
  }
  function measure() {
    if(resizeQueued)return;resizeQueued=true;
    requestAnimationFrame(()=>{resizeQueued=false;const target=snapshot?.collapsed?q('.nl-capsule'):q('.nl-window');const bounds=target.getBoundingClientRect(),size={width:Math.round(bounds.width+24),height:Math.ceil(bounds.height+24)};const key=size.width+':'+size.height;if(key===lastSize||!nativeAvailable())return;lastSize=key;run('window.resize',size);});
  }
  for(const button of all('[data-panel]'))button.addEventListener('click',()=>setPanel(button.dataset.panel));
  for(const button of all('[data-close-panel]'))button.addEventListener('click',()=>setPanel(activePanel));
  for(const button of all('[data-switch]'))button.addEventListener('click',()=>{if(!keyboardAction&&snapshot?.[button.dataset.switch==='Windows'?'canSwitchWindows':'canSwitchMac']===true)return run('switch',{platform:button.dataset.switch});});
  q('[data-device-picker]').addEventListener('change',event=>{
    const picker=event.target,name=picker.value;
    if(deviceChanging||name===snapshot?.selectedDevice||!snapshot?.devices?.some(device=>device.name===name))return;
    deviceChanging=true;picker.disabled=true;picker.value=snapshot.selectedDevice||'';clearFrame();invalidateShared();
    void call('device.select',{name}).catch(error=>toast(error.message)).finally(()=>{deviceChanging=false;picker.disabled=false;if(snapshot){renderDeviceChoice(snapshot);acknowledgeDevice();}});
  });
  q('[data-start-sharing]').addEventListener('click',()=>changeKeyboard('start'));
  q('[data-preview-toggle]').addEventListener('change',async event=>{previewIntent=event.target.checked;if(!previewIntent){blockedFrameKey=frameKey(snapshot?.preview);clearFrame();}renderPreview();const requested=previewIntent;try{await call('preview.setEnabled',{enabled:requested});}catch(error){if(previewIntent===requested)previewIntent=null;renderPreview();toast(error.message);}});
  q('[data-clipboard-toggle]').addEventListener('change',async event=>{clipboardIntent=event.target.checked;const requested=clipboardIntent;try{await call('clipboard.setEnabled',{enabled:requested});}catch(error){if(clipboardIntent===requested)clipboardIntent=null;toast(error.message);}});
  let capsuleGesture=null,suppressCapsuleClick=false;
  q('[data-pin]').addEventListener('click',()=>run('window.setPinned',{pinned:!snapshot?.pinned}));q('[data-collapse]').addEventListener('click',()=>run('window.collapse'));q('[data-expand]').addEventListener('click',event=>{if(suppressCapsuleClick&&event.detail!==0){suppressCapsuleClick=false;event.preventDefault();return;}suppressCapsuleClick=false;run('window.expand');});
  q('[data-enlarge]').addEventListener('click',()=>run('preview.open'));q('[data-choose-files]').addEventListener('click',()=>run('files.choose'));q('[data-choose-folder]').addEventListener('click',()=>run('files.chooseFolder'));q('[data-open-inbox]').addEventListener('click',()=>run('inbox.open'));
  q('.nl-drag').addEventListener('pointerdown',event=>{if(snapshot?.pinned||event.button!==0||event.target.closest('input,select,button'))return;const bounds=root.getBoundingClientRect();run('window.drag',{surface:'header',anchor:{x:event.clientX-bounds.left,y:event.clientY-bounds.top}});});
  const capsule=q('.nl-capsule');
  capsule.addEventListener('pointerdown',event=>{capsuleGesture=null;suppressCapsuleClick=false;if(snapshot?.pinned||event.button!==0)return;const bounds=root.getBoundingClientRect();capsuleGesture={pointerId:event.pointerId,x:event.clientX,y:event.clientY,anchor:{x:event.clientX-bounds.left,y:event.clientY-bounds.top},dragged:false};capsule.setPointerCapture?.(event.pointerId);});
  capsule.addEventListener('pointermove',event=>{const gesture=capsuleGesture;if(!gesture||gesture.pointerId!==event.pointerId||gesture.dragged||snapshot?.pinned)return;if(event.buttons!==1){capsuleGesture=null;return;}const threshold=key=>Number.isFinite(snapshot?.[key])&&snapshot[key]>0?snapshot[key]:4;if(Math.abs(event.clientX-gesture.x)<threshold('dragThresholdX')&&Math.abs(event.clientY-gesture.y)<threshold('dragThresholdY'))return;gesture.dragged=true;suppressCapsuleClick=true;event.preventDefault();run('window.drag',{surface:'capsule',anchor:gesture.anchor});});
  capsule.addEventListener('pointerup',event=>{if(capsuleGesture?.pointerId===event.pointerId)capsuleGesture=null;});
  const cancelCapsuleGesture=event=>{if(capsuleGesture?.pointerId===event.pointerId){if(!capsuleGesture.dragged)suppressCapsuleClick=false;capsuleGesture=null;}};
  capsule.addEventListener('pointercancel',cancelCapsuleGesture);capsule.addEventListener('lostpointercapture',cancelCapsuleGesture);
  window.addEventListener('blur',()=>{capsuleGesture=null;});
  window.nianNativeDrag=over=>{dragDepth=over?1:0;q('[data-drag-overlay]').hidden=!over;if(over&&snapshot?.collapsed)run('window.expand');};
  window.nianPrepareTransfer=count=>{previousTransferIds=new Set((snapshot?.transfers||[]).map(item=>item.id));preparingTransfer=true;if(activePanel!=='transfer')setPanel('transfer');toast('正在准备发送 '+Math.max(1,Number(count)||1)+' 项…');};
  window.nianNativeTransferResult=result=>{preparingTransfer=false;toast(result?.ok?(result.queued===true?'已加入传输队列，可在这里查看进度。':'已发送到另一台电脑，原文件保留。'):result?.error||'发送未完成，请检查连接后重试。');};
  const hasFiles=event=>Array.from(event.dataTransfer?.types||[]).includes('Files')||event.dataTransfer?.files?.length>0;
  document.addEventListener('dragenter',event=>{if(!hasFiles(event))return;event.preventDefault();dragDepth++;q('[data-drag-overlay]').hidden=false;if(snapshot?.collapsed)run('window.expand');});document.addEventListener('dragover',event=>{if(!hasFiles(event))return;event.preventDefault();event.dataTransfer.dropEffect='copy';});document.addEventListener('dragleave',event=>{event.preventDefault();if(--dragDepth<=0){dragDepth=0;q('[data-drag-overlay]').hidden=true;}});
  document.addEventListener('drop',async event=>{event.preventDefault();dragDepth=0;q('[data-drag-overlay]').hidden=true;if(snapshot?.platform==='Mac')return;const files=Array.from(event.dataTransfer.files||[]);if(!files.length)return;window.nianPrepareTransfer(files.length);try{await call('files.drop',{names:files.map(file=>file.name)},files);preparingTransfer=false;toast('已加入传输队列，可在这里查看进度。');}catch(error){preparingTransfer=false;toast(error.message);}});
  q('[data-choose-receive]').onclick=()=>run('directory.choose',{kind:'receive'});
  q('[data-choose-receive-inline]').onclick=()=>run('directory.choose',{kind:'receive'});
  buildSettings();window.lucide?.createIcons({attrs:{width:16,height:16}});
  new ResizeObserver(measure).observe(root);window.addEventListener('resize',measure);
  const frameExpiry=setInterval(()=>{if(snapshot?.previewEnabled)renderPreview();},1000);window.addEventListener('pagehide',()=>clearInterval(frameExpiry));
  if(window.chrome?.webview)window.chrome.webview.addEventListener('message',event=>{const message=event.data;if(message.type==='state')window.nianState(message.state);else window.nianReceive(message);});
  run('ready').then(result=>{if(result?.epoch)currentEpoch=result.epoch;measure();});
})();
