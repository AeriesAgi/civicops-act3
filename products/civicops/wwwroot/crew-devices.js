'use strict';
// Uses the existing staff cookie/CSRF helper. Pairing secrets are shown once and never persisted.
(() => {
  const panel=document.querySelector('#crew-devices-panel');if(!panel)return;
  let loading=false;let identityKey='';let revealGeneration=0;
  const capture=()=>({epoch:state.authEpoch,name:state.auth?.name,tenantId:state.auth?.tenantId});
  const authorized=context=>context.epoch===state.authEpoch&&context.name===state.auth?.name&&context.tenantId===state.auth?.tenantId&&state.auth?.authenticated&&state.auth.roles?.some(role=>['supervisor','admin'].includes(role));
  const note=(text,bad=false)=>{const target=document.querySelector('#crew-device-result');target.textContent=text;target.className=bad?'error':'success';};
  async function refreshDevices() {
    const allowed=state.auth?.authenticated && state.auth.roles?.some(role=>['supervisor','admin'].includes(role));
    panel.hidden=!allowed;
    if(!allowed) { revealGeneration++;document.querySelector('#crew-pair-code').value='';document.querySelector('#crew-pair-reveal').hidden=true;document.querySelector('#crew-device-list').replaceChildren();document.querySelector('#crew-pair-worker').replaceChildren();identityKey='';return; }
    const context=capture();const key=context.epoch+':'+context.name+':'+context.tenantId;if(loading&&loading.epoch===context.epoch)return;loading=context;
    try {
      if(key!==identityKey) { revealGeneration++;document.querySelector('#crew-pair-code').value='';document.querySelector('#crew-pair-reveal').hidden=true; }
      const [crews,devices,policy]=await Promise.all([api('/api/crews',{},true),api('/api/v1/crew/devices',{},true),api('/api/v1/crew/policy',{},true)]);
      if(!authorized(context))return;
      identityKey=key;
      document.querySelector('#crew-pair-worker').innerHTML=crews.flatMap(crew=>(crew.memberIds||[]).map(id=>'<option value="'+esc(id)+'">'+esc(crew.name)+' · '+esc(id)+'</option>')).join('');
      const records=Array.isArray(devices)?devices:devices.devices || [];
      document.querySelector('#crew-device-list').innerHTML=records.map(device=>'<article class="admin-record"><strong>'+esc(device.deviceName)+'</strong><p class="hint">'+esc(device.workerId)+' · expires '+esc(date(device.expiresAt))+' · '+(device.revokedAt?'REVOKED':'ACTIVE')+'</p>'+(device.revokedAt?'':'<button class="secondary" type="button" data-revoke-crew-device="'+esc(device.id)+'">Revoke device access</button>')+'</article>').join('') || '<p class="hint">No paired crew devices in this municipality.</p>';
      for(const button of document.querySelectorAll('[data-revoke-crew-device]'))button.addEventListener('click',async()=>{const context=capture();button.disabled=true;try { const reason=document.querySelector('#crew-device-reason').value.trim();if(reason.length<5)throw new Error('Record an audit reason of at least 5 characters.');await api('/api/v1/crew/devices/'+encodeURIComponent(button.dataset.revokeCrewDevice)+'/revoke',{method:'POST',body:JSON.stringify({reason})},true);if(!authorized(context))return;note('Device access revoked and audited.');await refreshDevices(); }catch(error) { if(authorized(context))note(error.message,true); }finally { if(button.isConnected)button.disabled=false; }});
      for(const field of ['intervalSeconds','accuracyMeters','retentionDays','maxShiftHours'])document.querySelector('#crew-policy-'+field).value=policy[field];
    }catch(error) { if(authorized(context))note(error.message,true); }
    finally { if(loading===context)loading=false; }
  }
  document.querySelector('#crew-pair-form').addEventListener('submit',async event=>{event.preventDefault();const context=capture(),generation=++revealGeneration;const button=event.currentTarget.querySelector('button');button.disabled=true;document.querySelector('#crew-pair-code').value='';document.querySelector('#crew-pair-reveal').hidden=true;try { const issued=await api('/api/v1/crew/pairing',{method:'POST',body:JSON.stringify({workerId:document.querySelector('#crew-pair-worker').value,deviceName:document.querySelector('#crew-device-name').value.trim(),reason:document.querySelector('#crew-device-reason').value.trim()})},true);if(!authorized(context)||generation!==revealGeneration||document.visibilityState!=='visible'||state.view!=='control')return;document.querySelector('#crew-pair-code').value=issued.code;document.querySelector('#crew-pair-detail').textContent='Pairing ID '+issued.pairingId+' · municipality '+issued.tenantId+' · expires '+date(issued.expiresAt)+'. Supply all three fields directly to the named worker; do not email or publish the code.';document.querySelector('#crew-pair-code').type='text';document.querySelector('#crew-pair-reveal').hidden=false;setTimeout(()=>{if(authorized(context)&&generation===revealGeneration)document.querySelector('#crew-pair-dismiss').click();},Math.max(0,Date.parse(issued.expiresAt)-Date.now()));document.querySelector('#crew-pair-code').focus();note('One-use, short-lived pairing code issued and audited.'); }catch(error) { if(authorized(context))note(error.message,true); }finally { if(button.isConnected)button.disabled=false; }});
  document.querySelector('#crew-pair-dismiss').addEventListener('click',()=>{revealGeneration++;document.querySelector('#crew-pair-code').value='';document.querySelector('#crew-pair-detail').textContent='';document.querySelector('#crew-pair-reveal').hidden=true;});
  document.querySelector('#crew-devices-refresh').addEventListener('click',refreshDevices);
  document.querySelector('#crew-policy-form').addEventListener('submit',async event=>{event.preventDefault();const context=capture();const button=event.currentTarget.querySelector('button');button.disabled=true;try { const body={reason:document.querySelector('#crew-policy-reason').value.trim()};for(const field of ['intervalSeconds','accuracyMeters','retentionDays','maxShiftHours'])body[field]=Number(document.querySelector('#crew-policy-'+field).value);await api('/api/v1/crew/policy',{method:'PUT',body:JSON.stringify(body)},true);if(!authorized(context))return;note('Municipal crew privacy policy updated by the named supervisor and audited.');await refreshDevices(); }catch(error) { if(authorized(context))note(error.message,true); }finally { if(button.isConnected)button.disabled=false; }});
  // The main identity label changes on sign-in, role and municipality transitions.
  new MutationObserver(()=>refreshDevices()).observe(document.querySelector('#staff-summary'),{childList:true,characterData:true,subtree:true});
  document.querySelectorAll('[data-view]').forEach(button=>button.addEventListener('click',()=>{if(button.dataset.view!=='control')document.querySelector('#crew-pair-dismiss').click();}));
  document.addEventListener('visibilitychange',()=>{if(document.visibilityState!=='visible')document.querySelector('#crew-pair-dismiss').click();});
  refreshDevices();
})();
