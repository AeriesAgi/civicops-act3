'use strict';
// Local, tenant-authorized situation maps. No browser geolocation, tracking or person scoring.
window.CivicOpsMap = (() => {
  const q = selector => document.querySelector(selector);
  const model = { map:null, compact:null, config:null, features:[], heat:[], selected:null, markers:new Map(), groups:[], loading:false, reloadPending:false,
    stream:null, streamRetry:null, reloadTimer:null, fitted:false, generation:0, tileLayer:null, tileFailed:false, suggestions:null, dispatching:null, decision:null, selectionVersion:0 };
  const allowedLayers = ['wards','cases','crews','hazards','assets'];
  const priorityOrder = {Urgent:0,High:1,Medium:2,Low:3};
  const featureList = layer => model.features.filter(feature => feature.properties.layer === layer);
  const enabled = layer => q('#map-layer-' + layer)?.checked === true;
  const short = (value,length=65) => String(value || '').length > length ? String(value).slice(0,length-1) + '…' : String(value || '');
  const classWord = value => String(value || '').toLowerCase().replace(/[^a-z0-9-]/g,'');
  const minutesOld = at => Math.max(0,Math.floor((Date.now()-Date.parse(at))/60000));
  const age = at => { const minutes=minutesOld(at); return Number.isFinite(minutes) ? minutes<1 ? 'just now' : minutes<60 ? minutes+' min ago' : Math.floor(minutes/60)+' h '+minutes%60+' min ago' : 'time unavailable'; };
  const point = feature => [feature.geometry.coordinates[1],feature.geometry.coordinates[0]];
  const syntheticBoundaries = () => featureList('municipalities').some(feature=>feature.properties.synthetic);
  const municipalityName = () => featureList('municipalities')[0]?.properties.name?.replace(/\s*·\s*SYNTHETIC$/i,'') || 'Municipal operations';
  const locationLabel = properties => properties.synthetic ? 'Synthetic location' : 'Shared incident location, unverified';
  const crewSharing = properties => properties.onShift === true && properties.sharing === true;
  const crewLocationLabel = properties => crewSharing(properties)
    ? (properties.synthetic ? 'Synthetic crew-app ping' : 'Consented crew-app position') + ', on shift / sharing'
    : properties.synthetic ? 'Synthetic position' : 'Position sharing unavailable';
  const summary = message => { q('#map-summary').textContent=message; };
  function error(message) { q('#map-error').textContent=message; q('#map-error').hidden=!message; }
  function authorizationLost(failure,generation=model.generation) {
    if(generation!==model.generation || ![401,403].includes(failure?.status)) return false;
    clearStaffData();state.auth=null;state.token='';q('#dispatch-token').value='';q('[data-view="map"]').hidden=true;
    q('#map-locked p').textContent='Your municipal map access ended or was denied. Private locations and recommendations have been cleared. Sign in with an authorized staff role.';
    loadIdentity().catch(()=>{});return true;
  }
  function tileAttribution(config) { return esc(config.attribution || '').replace('OpenStreetMap contributors','<a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener noreferrer">OpenStreetMap contributors</a>'); }
  function bounds(config) { const box=config.bounds || config.bbox || [30.87,-30.02,31.07,-29.80]; return [[box[1],box[0]],[box[3],box[2]]]; }
  function shape(priority,small=false) {
    const p=classWord(priority); const drawing=p==='urgent' ? '<path class="symbol-fill" d="M16 2 L31 29 H1 Z"/>' : p==='high' ? '<path class="symbol-fill" d="M16 1 L31 16 L16 31 L1 16 Z"/>' : p==='medium' ? '<rect class="symbol-fill" x="3" y="3" width="26" height="26" rx="5"/>' : '<circle class="symbol-fill" cx="16" cy="16" r="13"/>';
    return `<svg class="map-case-symbol priority-${p}${small ? ' map-legend-icon' : ''}" viewBox="0 0 32 32" width="32" height="32" aria-hidden="true">${drawing}<text x="16" y="${p==='urgent' ? '20' : '17'}">${p==='urgent' ? '!' : p==='high' ? 'H' : p==='medium' ? 'M' : 'L'}</text></svg>`;
  }
  function caseIcon(properties) { const statuses={PendingApproval:'?',Approved:'✓',Assigned:'↗',InProgress:'••',WaitingForCitizen:'…',Resolved:'✓',Closed:'×'};return L.divIcon({className:`map-case-icon status-${classWord(properties.status)}${model.selected?.id===properties.id ? ' selected' : ''}`,html:shape(properties.priority)+`<span class="map-case-status" aria-hidden="true">${statuses[properties.status] || ''}</span>`,iconSize:[32,32],iconAnchor:[16,16]}); }
  function accessibleMarker(marker,label,featureId=null) {marker.options.civicFeatureId=featureId;marker.on('add',()=> { const element=marker.getElement(); if(element) { element.setAttribute('role','button'); element.setAttribute('aria-label',label);element.dataset.mapEssential='geographic-pin';if(featureId)element.dataset.mapEquivalent=featureId; } }); return marker; }
  function addGroup(group) { group.addTo(model.map); model.groups.push(group); return group; }
  function wardInfo(properties) {
    const card=q('#map-ward-info'); card.hidden=false;
    card.innerHTML=`<span class="eyebrow">${properties.synthetic ? 'SYNTHETIC WARD' : 'MUNICIPAL WARD'} · CURRENT SNAPSHOT</span><h3>${esc(properties.name)}</h3><div class="ward-numbers"><div><strong>${properties.openCases || 0}</strong><span>open cases</span></div><div><strong>${properties.slaBreaches || 0}</strong><span>review target breaches</span></div></div><p>Incident workload only. No individual or neighbourhood risk prediction.</p>`;
    summary(`${properties.name}, ${properties.synthetic ? 'synthetic' : 'municipal'} ward. ${properties.openCases || 0} open cases; ${properties.slaBreaches || 0} review target breaches.`);
  }
  function drawWards(features) {
    const group=L.geoJSON(features,{pane:'civic-wards',style:feature=>({className:'civic-ward',color:feature.properties.layer==='municipalities' ? '#a6d8b8' : '#79ad9c',weight:feature.properties.layer==='municipalities' ? 2 : 1.2,opacity:.83,fillColor:'#294c43',fillOpacity:feature.properties.layer==='municipalities' ? .13 : .18}),onEachFeature:(feature,layer)=> {
      if(feature.properties.layer!=='wards') return;
      layer.on('mouseover',()=>wardInfo(feature.properties));
      layer.on('mouseout',()=> { q('#map-ward-info').hidden=true; });
      layer.on('click',()=>wardInfo(feature.properties));
      layer.on('add',()=> { const element=layer.getElement(); if(!element) return; element.setAttribute('tabindex','0'); element.setAttribute('role','button'); element.setAttribute('aria-label',`${feature.properties.name}, ${feature.properties.synthetic ? 'synthetic' : 'municipal'} ward, ${feature.properties.openCases || 0} open cases and ${feature.properties.slaBreaches || 0} review target breaches. Press Enter for statistics.`); element.addEventListener('focus',()=>wardInfo(feature.properties)); element.addEventListener('blur',()=>{q('#map-ward-info').hidden=true;}); element.addEventListener('keydown',event=>{if(['Enter',' '].includes(event.key)) {event.preventDefault();wardInfo(feature.properties);} if(event.key==='Escape') q('#map-ward-info').hidden=true;}); });
    }});
    addGroup(group);
    if(enabled('wards')) for(const feature of features.filter(item=>item.properties.layer==='wards')) {
      const centroid=L.geoJSON(feature).getBounds().getCenter();
      addGroup(L.marker(centroid,{pane:'civic-labels',interactive:false,keyboard:false,icon:L.divIcon({className:'map-ward-label',html:`<span>${esc(feature.properties.name.match(/^Ward \d+/)?.[0].toUpperCase() || short(feature.properties.name.replace(/\s*·\s*SYNTHETIC$/i,''),22))}</span>`,iconSize:[130,36],iconAnchor:[65,18]})}));
    }
  }
  // Heat cells arrive aggregated by the server; this layer never receives resident/worker tracks.
  const GridHeatLayer = L.Layer.extend({
    initialize(features) { this.features=features; },
    onAdd(map) { this.map=map; this.canvas=L.DomUtil.create('canvas','civic-map-heat',map.getPane('civic-heat')); this.canvas.setAttribute('aria-hidden','true'); map.on('moveend zoomend resize',this.redraw,this); this.redraw(); },
    onRemove(map) { map.off('moveend zoomend resize',this.redraw,this); this.canvas.remove(); },
    redraw() {
      const size=this.map.getSize(); const origin=this.map.containerPointToLayerPoint([0,0]); L.DomUtil.setPosition(this.canvas,origin); this.canvas.width=size.x; this.canvas.height=size.y;
      const context=this.canvas.getContext('2d'); if(!context) return;
      for(const cell of this.features) { const position=this.map.latLngToContainerPoint(point(cell)); const weight=Math.min(1,Math.max(.12,Number(cell.properties.weight || cell.properties.count/10))); const radius=32+weight*45; const gradient=context.createRadialGradient(position.x,position.y,0,position.x,position.y,radius); gradient.addColorStop(0,`rgba(255,117,81,${weight*.7})`); gradient.addColorStop(.42,`rgba(246,188,75,${weight*.42})`); gradient.addColorStop(.75,`rgba(93,198,161,${weight*.25})`); gradient.addColorStop(1,'rgba(93,198,161,0)'); context.fillStyle=gradient;context.fillRect(position.x-radius,position.y-radius,radius*2,radius*2); }
    }
  });
  function visibleCases() { const windowHours=q('#map-window').value==='7d' ? 168 : 24; const cutoff=Date.now()-windowHours*3600000; return featureList('cases').filter(feature=>Date.parse(feature.properties.createdAt)>=cutoff); }
  function redraw() {
    if(!model.map) return;
    for(const group of model.groups) model.map.removeLayer(group); model.groups=[]; model.markers.clear();
    const outlines=model.features.filter(feature=>feature.properties.layer==='municipalities' || feature.properties.layer==='wards' && enabled('wards'));
    drawWards(outlines);
    if(enabled('heatmap')) addGroup(new GridHeatLayer(model.heat));
    if(enabled('cases')) {
      const clusters=L.markerClusterGroup({maxClusterRadius:43,disableClusteringAtZoom:16,showCoverageOnHover:false,animate:false,spiderfyOnMaxZoom:true,zoomToBoundsOnClick:true,iconCreateFunction:cluster=> {const members=cluster.getAllChildMarkers().map(marker=>marker.options.civicFeatureId);const icon=L.divIcon({html:`<div><span>${cluster.getChildCount()}</span></div>`,className:'marker-cluster',iconSize:[42,42]});const create=icon.createIcon.bind(icon);icon.createIcon=previous=>{const element=create(previous);element.setAttribute('aria-label',cluster.getChildCount()+' incident locations. Activate to expand, or browse the complete feature list.');element.dataset.mapEssential='geographic-pin';element.dataset.mapClusterMembers=members.join(',');return element;};return icon;}});
      for(const feature of visibleCases()) { const p=feature.properties; const label=`${p.priority} case ${p.referenceNumber}, ${p.status}, ${p.title}. ${locationLabel(p)}. Press Enter to review.`;
        const marker=accessibleMarker(L.marker(point(feature),{icon:caseIcon(p),title:label,keyboard:true}),label,p.id); marker.on('click',()=>select(feature)); marker.bindTooltip(esc(`${p.referenceNumber} · ${p.priority} · ${p.status}`),{direction:'top'}); clusters.addLayer(marker); model.markers.set(p.id,marker);
      }
      clusters.on('clusterclick',event=>summary(`Cluster contains ${event.layer.getChildCount()} incident locations. Zooming to show the cases.`));
      addGroup(clusters);
    }
    if(enabled('crews')) for(const feature of featureList('crews')) {
      const p=feature.properties; const stale=minutesOld(p.lastSeenAt)>Number(model.config.crewStaleMinutes || 10); const available=String(p.availability).toLowerCase()==='available'; const status=available ? 'available' : /busy|assigned|inprogress/i.test(p.availability+' '+p.status) ? 'busy' : 'unavailable';
      const abbreviation=/road/i.test(p.department) ? 'RO' : /water/i.test(p.department) ? 'WA' : /electric/i.test(p.department) ? 'EL' : /refuse|waste/i.test(p.department) ? 'RE' : 'CR';
      const label=`${p.name}, ${p.availability || p.status}, last seen ${age(p.lastSeenAt)}${stale ? ', stale position' : ''}. ${crewLocationLabel(p)}. Press Enter for detail.`;
      const marker=accessibleMarker(L.marker(point(feature),{icon:L.divIcon({className:`map-crew-icon ${status}${stale ? ' stale' : ''}`,html:`<span class="map-crew-symbol">${abbreviation}</span>`,iconSize:[31,31],iconAnchor:[15,15]}),keyboard:true,title:label,zIndexOffset:200}),label,p.id);
      marker.on('click',()=>select(feature)); marker.bindTooltip(esc(`${p.name} · ${p.availability || p.status} · ${age(p.lastSeenAt)}${crewSharing(p) ? ' · ON SHIFT / SHARING' : ''}${stale ? ' · STALE' : ''}`)); addGroup(marker); model.markers.set(p.id,marker);
    }
    if(enabled('hazards')) for(const feature of featureList('hazards')) {
      const p=feature.properties; const symbol=p.eventType==='fire' ? 'F' : p.eventType==='flooding' ? '≈' : p.eventType==='live-wire-down' || p.eventType==='downed-line' ? 'ϟ' : '!';
      addGroup(L.circle(point(feature),{radius:Number(p.exclusionRadiusMeters || 50),color:'#efb080',weight:1.5,dashArray:'4 5',fillColor:'#e99c64',fillOpacity:.12,interactive:false}));
      const label=`${p.title}, ${p.eventType}, precaution radius ${p.exclusionRadiusMeters} metres, ${p.synthetic ? 'synthetic' : 'reported'} hazard. Press Enter for detail.`;
      const marker=accessibleMarker(L.marker(point(feature),{icon:L.divIcon({className:'map-hazard-icon',html:`<span class="map-hazard-symbol"><span>${symbol}</span></span>`,iconSize:[32,32],iconAnchor:[16,16]}),keyboard:true,title:label,zIndexOffset:300}),label,p.id);
      marker.on('click',()=>select(feature)); marker.bindTooltip(esc(`${p.title} · ${p.exclusionRadiusMeters} m precaution radius`)); addGroup(marker); model.markers.set(p.id,marker);
    }
    if(enabled('assets')) for(const feature of featureList('assets')) {
      const p=feature.properties; const label=`${p.name || p.title}, ${p.synthetic ? 'synthetic' : 'municipal'} ${p.assetType || 'municipal asset'}. Press Enter for detail.`;
      const marker=accessibleMarker(L.marker(point(feature),{icon:L.divIcon({className:'map-asset-icon',html:'<span class="map-asset-symbol">▣</span>',iconSize:[24,24],iconAnchor:[12,12]}),title:label,keyboard:true}),label,p.id); marker.on('click',()=>select(feature)); marker.bindTooltip(esc(p.name || p.title)); addGroup(marker);
    }
    updateRail();
  }
  function updateRail() {
    for(const layer of allowedLayers) q('#map-count-'+layer).textContent=layer==='cases' ? visibleCases().length : featureList(layer).length;
    const cases=visibleCases().slice().sort((a,b)=>(priorityOrder[a.properties.priority]-priorityOrder[b.properties.priority]) || Date.parse(a.properties.createdAt)-Date.parse(b.properties.createdAt));
    q('#map-priority-count').textContent=cases.filter(feature=>['Urgent','High'].includes(feature.properties.priority) && !['Resolved','Closed'].includes(feature.properties.status)).length+' high / urgent';
    q('#map-cases').innerHTML=cases.filter(feature=>!['Closed','Resolved'].includes(feature.properties.status)).slice(0,18).map(feature=>{const p=feature.properties;return `<button class="map-case-row${model.selected?.id===p.id ? ' selected' : ''}" data-map-case="${esc(p.id)}"><span>${esc(p.referenceNumber)}<span class="map-priority-word ${classWord(p.priority)}">${esc(p.priority)}</span></span><strong>${esc(short(p.title,66))}</strong><small>${esc(p.department)} · ${esc(p.status)}</small></button>`;}).join('') || '<p class="map-empty">No geolocated cases in this window. Load the synthetic evening scenario or share a case location with consent.</p>';
    q('#map-cases').querySelectorAll('[data-map-case]').forEach(button=>button.addEventListener('click',()=> { const feature=featureList('cases').find(item=>item.properties.id===button.dataset.mapCase); select(feature); const marker=model.markers.get(feature.properties.id); if(marker && marker.__parent) marker.__parent._group.zoomToShowLayer(marker,()=>model.map.panTo(marker.getLatLng())); else model.map.panTo(point(feature)); }));
    q('#map-ward-list').innerHTML=featureList('wards').map(feature=>`<li><button data-map-ward="${esc(feature.properties.id)}">${esc(feature.properties.name)} · ${feature.properties.openCases} open</button></li>`).join('');
    q('#map-ward-list').querySelectorAll('[data-map-ward]').forEach(button=>button.addEventListener('click',()=> { const feature=featureList('wards').find(item=>item.properties.id===button.dataset.mapWard); model.map.fitBounds(L.geoJSON(feature).getBounds(),{maxZoom:14}); wardInfo(feature.properties); }));
    q('#map-heat-legend').hidden=!enabled('heatmap');q('#map-heat-legend-title').textContent=(q('#map-heat-metric').value==='cases' ? 'Case density' : 'Hazard density')+' · '+q('#map-window').value;q('#map-heat-grid').textContent='Aggregated '+(model.heat[0]?.properties.cellSizeDegrees || .008)+'° grid · relative incident count';
    renderFeatureBrowser();
    const counts=allowedLayers.map(layer=>`${layer==='cases' ? visibleCases().length : featureList(layer).length} ${layer}${!enabled(layer) ? ' (layer hidden)' : ''}`).join(', ');
    q('#map-visible-count').textContent=`${visibleCases().length} cases · ${featureList('crews').length} crews · ${featureList('hazards').length} hazards`;
    q('#map-municipality-name').textContent=municipalityName().toUpperCase(); q('#map-context-name').textContent=municipalityName().toUpperCase(); q('#map-geography-badge').textContent=syntheticBoundaries() ? 'SYNTHETIC BOUNDARIES' : 'MUNICIPAL BOUNDARIES'; q('#map-context-note').textContent=syntheticBoundaries() ? 'FICTIONAL BOUNDARIES · KZN COAST' : 'CONFIGURED MUNICIPAL GEOGRAPHY';
    summary(`${municipalityName()} situation map. ${syntheticBoundaries() ? 'Synthetic boundaries. ' : ''}${counts}. Priority uses incident hazards only. Every dispatch requires authorized human approval. Keyboard users can review the incident list and ward overview.`);
    if(!model.selected) renderSnapshot();
  }
  function renderFeatureBrowser() {
    const filter=q('#map-accessible-filter').value;const cases=visibleCases();const list=[...cases,...model.features.filter(feature=>['crews','hazards','assets','wards'].includes(feature.properties.layer))].filter(feature=>!filter || feature.properties.layer===filter);
    q('#map-accessible-count').textContent=list.length;
    q('#map-accessible-items').innerHTML=list.map(feature=> {const p=feature.properties;const title=p.referenceNumber ? p.referenceNumber+' · '+p.title : p.name || p.title;return `<button type="button" data-map-feature="${esc(p.id)}"><strong>${esc(title)}</strong><span>${esc(p.layer)} · ${esc(p.status || p.availability || p.eventType || p.assetType || (p.openCases+' open cases'))}</span></button>`;}).join('') || '<p class="map-empty">No features match this viewport and filter.</p>';
    q('#map-accessible-items').querySelectorAll('[data-map-feature]').forEach(button=>button.addEventListener('click',async()=> {const feature=model.features.find(item=>item.properties.id===button.dataset.mapFeature);if(!feature)return;if(feature.properties.layer==='wards'){wardInfo(feature.properties);model.map.fitBounds(L.geoJSON(feature).getBounds(),{maxZoom:14,animate:false});}else {const generation=model.generation;await select(feature);if(generation!==model.generation)return;model.map.setView(point(feature),Math.max(15,model.map.getZoom()),{animate:false});}q('#map-inspector').scrollIntoView({block:'nearest'});}));
  }
  function renderSnapshot() {
    const cases=visibleCases(); const open=cases.filter(item=>!['Resolved','Closed'].includes(item.properties.status)); const crews=featureList('crews');
    q('#map-inspector').innerHTML=`<span class="eyebrow">MUNICIPAL SITUATION PICTURE</span><h2>${model.config.demoMode ? 'Evening in<br>'+esc(municipalityName())+'.' : 'Municipal situation<br>picture.'}</h2><p>Service incidents, qualified response units and infrastructure hazards.</p><span class="map-badge">${model.features.every(feature=>feature.properties.synthetic) ? 'SYNTHETIC DATA' : 'MUNICIPAL INCIDENT DATA'}</span><div class="map-snapshot"><div><strong id="map-open-count">${open.length}</strong><span>open geolocated cases</span></div><div><strong>${crews.filter(item=>String(item.properties.availability).toLowerCase()==='available').length}</strong><span>${crews.every(item=>item.properties.synthetic) ? 'available simulated crews' : 'available crews'}</span></div><div><strong>${cases.filter(item=>item.properties.slaBreached).length}</strong><span>review target breaches</span></div><div><strong>${featureList('hazards').length}</strong><span>active hazard events</span></div></div><h3>Hazards to review</h3><div class="map-hazard-list">${featureList('hazards').slice(0,4).map(feature=>`<div class="map-hazard-item"><strong>${esc(feature.properties.title)}</strong><span>${esc(feature.properties.eventType)} · ${feature.properties.exclusionRadiusMeters} m precaution radius</span></div>`).join('') || '<p class="map-empty">No hazard events in this view.</p>'}</div><p class="hint">Select a case to review a crew suggestion. Human confirmation required.</p>`;
  }
  async function select(feature,refreshing=false) {
    if(!feature) return;
    const p=feature.properties; const generation=model.generation;const selectionVersion=++model.selectionVersion;const sameCase=model.selected?.id===p.id && p.layer==='cases';const preserve=refreshing && sameCase && model.selected.version===p.version && model.selected.status===p.status;const draft=sameCase ? {note:q('#map-dispatch-note')?.value || '',crewId:q('#map-crew-choice')?.value || ''} : null; model.selected=p;if(!preserve)model.suggestions=null; redraw();
    const inspector=q('#map-inspector');
    if(p.layer==='cases') {
      if(!preserve) {inspector.innerHTML=`<span class="eyebrow">INCIDENT DETAIL · ${esc(p.referenceNumber)}</span><h2>${esc(p.title)}</h2><span class="map-badge ${p.priority==='Urgent' || p.priority==='High' ? 'warning' : 'muted'}">${esc(p.priority)}</span><span class="map-badge muted">${esc(p.status)}</span>${p.synthetic ? '<span class="map-badge">SYNTHETIC</span>' : '<span class="map-badge warning">SHARED · UNVERIFIED</span>'}<div class="map-detail-meta"><div><span>Department</span>${esc(p.department)}</div><div><span>Ward</span>${esc(featureList('wards').find(item=>item.properties.id===p.wardId)?.properties.name || p.wardId || 'Outside boundaries')}</div><div><span>Last update</span>${esc(age(p.updatedAt))}</div><div><span>Review target</span>${p.slaBreached ? 'Target breached' : 'Within target / reviewed'}</div></div><button id="map-open-case" class="map-full-button">Open case workspace ↗</button><div id="map-nearest" class="map-nearest"><h3>Nearest qualified crew</h3><p>Checking skills, certification and availability…</p></div>`;
      q('#map-open-case').addEventListener('click',()=>openCase(p.id));}
      summary(`${p.priority} incident ${p.referenceNumber}, ${p.title}, ${p.status}. ${p.department}. ${locationLabel(p)}. Crew recommendation requires human review.`);
      try {
        const response=await api(`/api/v1/map/cases/${encodeURIComponent(p.id)}/nearest-crews`,{},true);
        if(generation!==model.generation || model.selected?.id!==p.id || selectionVersion!==model.selectionVersion) return;
        model.suggestions=response;
        const currentDraft=q('#map-dispatch-note') ? {note:q('#map-dispatch-note').value,crewId:q('#map-crew-choice')?.value || ''} : draft;renderNearest(p,response,currentDraft);
      } catch(failure) { if(authorizationLost(failure,generation)) return; if(generation===model.generation && model.selected?.id===p.id && selectionVersion===model.selectionVersion) q('#map-nearest').innerHTML=`<h3>Nearest qualified crew</h3><p>${esc(failure.message)}</p>`; }
    } else if(p.layer==='crews') {
      const stale=minutesOld(p.lastSeenAt)>Number(model.config.crewStaleMinutes || 10);
      inspector.innerHTML=`<span class="eyebrow">CREW DETAIL · ${esc(crewLocationLabel(p).toUpperCase())}</span><h2>${esc(p.name)}</h2><span class="map-badge ${stale ? 'warning' : ''}">${stale ? 'STALE POSITION' : esc(p.availability || p.status)}</span><div class="map-detail-meta"><div><span>Department</span>${esc(p.department)}</div><div><span>Availability</span>${esc(p.availability || p.status)}</div><div><span>Last seen</span>${esc(age(p.lastSeenAt))}</div><div><span>Location source</span>${crewSharing(p) ? 'Crew app · shift and consent verified' : 'Seeded simulator'}</div>${crewSharing(p) ? '<div><span>Sharing state</span>ON SHIFT / SHARING</div>' : ''}</div><h3>Recorded skills</h3><p>${esc((p.skills || []).join(' · ') || 'Review full qualification evidence before assignment.')}</p><button id="map-crew-qualifications" class="map-full-button">View crew qualification evidence</button><div id="map-crew-evidence"></div><h3>Location privacy</h3><p>${crewSharing(p) ? 'This position was accepted with an active shift and explicit consent. Sharing stops when the shift ends or consent is withdrawn. Supervisors see current positions; workers control their own sharing.' : 'This is a synthetic seeded crew position. Crew-app sharing requires an active shift, explicit revocable consent and a visible sharing indicator.'}</p>`;
      q('#map-crew-qualifications').addEventListener('click',async event=> { event.currentTarget.disabled=true; try { const evidence=await api('/api/v1/map/crews/'+encodeURIComponent(p.id),{},true); if(generation!==model.generation || model.selected?.id!==p.id) return; q('#map-crew-evidence').innerHTML='<h3>Skill evidence</h3><p>'+esc(evidence.qualifications.map(item=>item.code+' · level '+item.level).join('; '))+'</p><h3>Certification evidence</h3>'+evidence.certifications.map(cert=>'<div class="map-hazard-item"><strong>'+esc(cert.code)+'</strong><span>'+esc(cert.source)+' · valid until '+esc(date(cert.expiresAt))+'</span></div>').join(''); } catch(failure) { if(authorizationLost(failure,generation)) return; if(generation===model.generation && model.selected?.id===p.id) q('#map-crew-evidence').textContent=failure.message; } finally { if(generation===model.generation && model.selected?.id===p.id) q('#map-crew-qualifications').disabled=false; } });
      summary(`${p.name}, ${p.department}, ${p.availability || p.status}, last seen ${age(p.lastSeenAt)}${stale ? ', stale' : ''}. ${crewLocationLabel(p)}.`);
    } else if(p.layer==='hazards') {
      if(model.map.getZoom()<15) model.map.setView(point(feature),15);
      inspector.innerHTML=`<span class="eyebrow">SITUATION EVENT · ${p.synthetic ? 'SYNTHETIC' : 'HUMAN REVIEW REQUIRED'}</span><h2>${esc(p.title)}</h2><span class="map-badge warning">${esc(p.eventType)}</span><div class="map-detail-meta"><div><span>Precaution radius</span>${p.exclusionRadiusMeters} metres</div><div><span>Event time</span>${esc(age(p.occurredAt))}</div><div><span>Confidence</span>${Math.round((p.confidence || 0)*100)}%${p.synthetic ? ' · synthetic' : ' · event confidence'}</div><div><span>Action</span>Human review</div></div><p>The outline is a precautionary ${p.synthetic ? 'scenario' : 'reported'} radius, not a verified cordon. A qualified human assesses the situation and decides every safety or dispatch action.</p><h3>Object and situation events only</h3><p>No faces, biometrics, personal suspicion scores or predictive policing. CivicOps stores structured incident events; video frames are never stored.</p>`;
      summary(`${p.synthetic ? 'Synthetic' : 'Reported'} hazard ${p.title}, ${p.eventType}. ${p.exclusionRadiusMeters} metre precaution radius. Human assessment required.`);
    } else {
      inspector.innerHTML=`<span class="eyebrow">MUNICIPAL ASSET${p.synthetic ? ' · SYNTHETIC' : ''}</span><h2>${esc(p.name || p.title)}</h2><span class="map-badge muted">${esc(p.assetType || 'Infrastructure asset')}</span><p>${p.synthetic ? 'Fictional municipal infrastructure used for scenario orientation. These are not real addresses, assets or critical infrastructure coordinates.' : 'Municipal infrastructure shown for authorized incident-response coordination.'}</p>`;
      summary(`${p.synthetic ? 'Synthetic' : 'Municipal'} asset ${p.name || p.title}, ${p.assetType || 'municipal infrastructure'}.`);
    }
  }
  function renderNearest(incident,response,draft=null) {
    const target=q('#map-nearest'); const candidates=response.suggestions || [];const active=document.activeElement;const focus=active?.id==='map-dispatch-note' ? {id:active.id,start:active.selectionStart,end:active.selectionEnd} : active?.id==='map-crew-choice' ? {id:active.id} : null;
    target.innerHTML=`<h3>Nearest qualified crew</h3><p>Straight-line distance · skills + certificates + availability. No route or arrival-time promise.</p>${candidates.length ? `<label for="map-crew-choice">Qualified response unit</label><select id="map-crew-choice">${candidates.map(item=>`<option value="${esc(item.crewId)}">${esc(item.name)} · ${Number(item.distanceKm).toFixed(2)} km</option>`).join('')}</select><div id="map-nearest-card"></div>` : '<p>No qualified available crew has a usable position. Escalate to the municipal supervisor.</p>'}${response.requiresCaseApproval ? '<p class="map-decision">Review and approve this case in its workspace before confirming a crew assignment.</p><button id="map-review-first" class="map-full-button">Review case before dispatch ↗</button>' : candidates.length && hasPermission('dispatch') ? `<form id="map-dispatch-form"><label for="map-dispatch-note">Reason for this human decision</label><textarea id="map-dispatch-note" maxlength="2000" minlength="5" required rows="3" placeholder="Operational reason only; avoid personal details."></textarea><p>Your server-verified staff identity, time and reason are recorded in the audit trail.</p><button class="map-primary map-full-button" type="submit"${model.dispatching===incident.id ? ' disabled' : ''}>Confirm crew assignment</button></form>` : ''}<div id="map-decision-result" class="map-decision" role="status" aria-live="polite">${model.decision?.id===incident.id ? esc(model.decision.text) : ''}</div>`;
    const showCandidate=()=> { const item=candidates.find(candidate=>candidate.crewId===q('#map-crew-choice')?.value); if(!item) return; q('#map-nearest-card').innerHTML=`<div class="map-nearest-card"><strong>${esc(item.name)}</strong><span>${Number(item.distanceKm).toFixed(2)} km · ${esc(item.availability)} · ${esc(item.status || 'eligible')}</span><ul>${(item.qualifications || item.qualificationReasons || []).slice(0,4).map(reason=>`<li>${esc(reason)}</li>`).join('')}</ul></div>`; };
    if(draft?.crewId && candidates.some(item=>item.crewId===draft.crewId)) q('#map-crew-choice').value=draft.crewId; if(q('#map-dispatch-note') && draft) q('#map-dispatch-note').value=draft.note;
    q('#map-crew-choice')?.addEventListener('change',showCandidate); showCandidate();
    q('#map-review-first')?.addEventListener('click',()=>openCase(incident.id));
    q('#map-dispatch-form')?.addEventListener('submit',async event=> {
      event.preventDefault(); const button=event.currentTarget.querySelector('button'); if(button.disabled || model.dispatching===incident.id) return; button.disabled=true;model.dispatching=incident.id; const generation=model.generation;
      const request={crewId:q('#map-crew-choice').value,expectedVersion:responseVersion(),note:q('#map-dispatch-note').value.trim()};
      try { const result=await api(`/api/v1/map/cases/${encodeURIComponent(incident.id)}/dispatch`,{method:'POST',body:JSON.stringify(request)},true); if(generation!==model.generation) return; model.decision={id:incident.id,text:`Assignment recorded for ${result.incident?.assignedCrewName || 'selected crew'}. Your human decision was audited.`}; if(model.selected?.id===incident.id && q('#map-decision-result')) q('#map-decision-result').textContent=model.decision.text; await reload(); if(generation===model.generation)summary('Crew assignment confirmed by authorized staff and audited.'); }
      catch(failure) { if(authorizationLost(failure,generation)) return; if(generation===model.generation) {model.decision={id:incident.id,text:failure.message};if(model.selected?.id===incident.id && q('#map-decision-result')) q('#map-decision-result').textContent=failure.message;} }
      finally { if(generation===model.generation) {model.dispatching=null;if(button.isConnected) button.disabled=false;else if(model.selected?.id===incident.id && q('#map-dispatch-form button'))q('#map-dispatch-form button').disabled=false;} }
    });
    if(focus && q('#'+focus.id)) {const element=q('#'+focus.id);element.focus({preventScroll:true});if(focus.start!==undefined)element.setSelectionRange(focus.start,focus.end);}
    function responseVersion() { return response.caseVersion || incident.version; }
  }
  async function openCase(id) {
    const generation=model.generation;
    try { const incident=await api('/api/dispatch/incidents/'+encodeURIComponent(id),{},true); if(generation!==model.generation) return; setView('control'); location.hash='control'; renderDetail(incident); q('#case-detail').scrollIntoView({block:'start',behavior:'auto'}); q('#case-detail').setAttribute('tabindex','-1'); q('#case-detail').focus({preventScroll:true}); }
    catch(failure) { if(authorizationLost(failure,generation)) return; if(generation===model.generation) error(failure.message); }
  }
  // Leaflet 1.9.4 cancels tiles with a data: GIF. Remove the source instead so strict img-src stays limited to self/configured host.
  const StrictTileLayer=L.TileLayer.extend({
    _removeTile(key) {const tile=this._tiles[key];if(!tile)return;tile.el.onload=L.Util.falseFn;tile.el.onerror=L.Util.falseFn;tile.el.removeAttribute('src');return L.GridLayer.prototype._removeTile.call(this,key);},
    _abortLoading() {for(const key of Object.keys(this._tiles)){const tile=this._tiles[key];if(tile.coords.z===this._tileZoom)continue;tile.el.onload=L.Util.falseFn;tile.el.onerror=L.Util.falseFn;if(!tile.el.complete){tile.el.removeAttribute('src');L.DomUtil.remove(tile.el);delete this._tiles[key];this.fire('tileabort',{tile:tile.el,coords:tile.coords});}}}
  });
  function addTiles(map,config,compact=false) {
    if(config.tileEnabled===false || !config.tileUrl || config.tileUrl==='none') { if(!compact) q('#map-outline-label').textContent='OUTLINE MODE · tile layer disabled'; return; }
    const tiles=new StrictTileLayer(config.tileUrl,{attribution:tileAttribution(config),maxZoom:19,keepBuffer:0,updateWhenIdle:true,referrerPolicy:'origin',crossOrigin:false});
    tiles.on('tileerror',()=> { if(map.hasLayer(tiles)) map.removeLayer(tiles); if(!compact) { model.tileFailed=true; q('#map-outline-label').textContent='OUTLINE MODE · tile service unavailable'; summary('Map tiles are unavailable. Configured municipality and ward outlines remain visible.'); } });
    tiles.addTo(map); if(!compact) { model.tileLayer=tiles; q('#map-outline-label').textContent='BASE TILES · '+(config.attribution || 'configured tile source'); }
  }
  function initializeMap(config) {
    model.map=L.map('ops-map',{zoomControl:false,preferCanvas:false,zoomAnimation:false,fadeAnimation:false,markerZoomAnimation:false,zoomSnap:.25,zoomDelta:.5,minZoom:4,maxZoom:19,maxBounds:[[-35,16],[-22,33]],maxBoundsViscosity:1,scrollWheelZoom:true,attributionControl:true});
    model.map.attributionControl.setPrefix(false); model.map.attributionControl.addAttribution('Configured boundaries · CivicOps');
    model.map.createPane('civic-wards').style.zIndex='250'; model.map.createPane('civic-heat').style.zIndex='350'; model.map.createPane('civic-labels').style.zIndex='390';
    L.control.zoom({position:'topright'}).addTo(model.map); L.control.scale({position:'bottomleft',imperial:false}).addTo(model.map); addTiles(model.map,config);
    model.map.fitBounds(bounds(config),{padding:[28,28]}); model.fitted=true;
    model.map.on('moveend',()=> { if(state.view!=='map') return; clearTimeout(model.reloadTimer); model.reloadTimer=setTimeout(()=>reload(),400); });
    model.map.on('keydown',event=> { if(event.originalEvent.key==='Escape') q('#map-ward-info').hidden=true; });
  }
  async function reload() {
    if(!model.map || !hasPermission('operations-read')) return;
    if(model.loading) {model.reloadPending=true;return;}
    model.loading=true; const generation=model.generation;
    try {
      const viewport=model.map.getBounds(); const bbox=[Math.max(16,viewport.getWest()),Math.max(-35,viewport.getSouth()),Math.min(33,viewport.getEast()),Math.min(-22,viewport.getNorth())].map(value=>value.toFixed(6)).join(',');
      const requested=['municipalities','wards',...allowedLayers.filter(layer=>layer!=='wards')].join(','); let page=1; const features=[]; let total=0;
      while(page && page<=8) { const collection=await api(`/api/v1/map/layers?bbox=${encodeURIComponent(bbox)}&layers=${requested}&limit=500&page=${page}`,{},true); if(generation!==model.generation) return; features.push(...collection.features); total=collection.total; page=collection.nextPage; }
      model.features=features;
      if(enabled('heatmap')) { const heat=await api(`/api/v1/map/heatmap?metric=${q('#map-heat-metric').value}&window=${q('#map-window').value}`,{},true); if(generation!==model.generation) return; model.heat=heat.features; } else model.heat=[];
      redraw(); error(total>features.length ? `Showing ${features.length} of ${total} features in the viewport. Zoom in to review a smaller area.` : '');
      q('#map-refreshed').textContent='Updated '+new Date().toLocaleTimeString('en-ZA',{hour:'2-digit',minute:'2-digit'});
      if(model.selected) { const current=features.find(feature=>feature.properties.id===model.selected.id); if(current) await select(current,true); else { model.selected=null; renderSnapshot(); } }
    } catch(failure) { if(authorizationLost(failure,generation)) return; if(generation===model.generation) error(failure.message); }
    finally { if(generation===model.generation) {model.loading=false;if(model.reloadPending){model.reloadPending=false;queueMicrotask(()=>reload());}} }
  }
  function connection(connected,message) { q('#map-sync').classList.toggle('connected',connected); q('#map-stream-status').textContent=message; }
  async function stream() {
    model.stream?.abort(); clearTimeout(model.streamRetry); const controller=new AbortController(); model.stream=controller; const generation=model.generation;
    try {
      const headers=state.token ? {Authorization:'Bearer '+state.token} : {};
      const response=await fetch(localPath('/api/v1/map/stream'),{headers,signal:controller.signal,credentials:'same-origin',cache:'no-store'});
      if(generation!==model.generation || controller.signal.aborted) return;
      if(!response.ok || !response.body) {const failure=new Error('Live updates unavailable');failure.status=response.status;throw failure;} connection(true,'Live updates connected');
      const reader=response.body.getReader(); const decoder=new TextDecoder(); let buffer='';
      while(!controller.signal.aborted) { const next=await reader.read(); if(next.done) break; buffer+=decoder.decode(next.value,{stream:true}); let end; while((end=buffer.indexOf('\n\n'))>=0) { const message=buffer.slice(0,end);buffer=buffer.slice(end+2);if(/event: (refresh|map\.update)/.test(message)) { clearTimeout(model.reloadTimer); model.reloadTimer=setTimeout(()=>reload(),300); } } if(buffer.length>65536) buffer=''; }
    } catch(failure) { authorizationLost(failure,generation); /* Bounded polling remains available for temporary transport loss. */ }
    finally { if(generation===model.generation && state.view==='map' && !controller.signal.aborted) { connection(false,'Polling · stream reconnecting'); model.streamRetry=setTimeout(()=>stream(),5000); } }
  }
  async function show() {
    q('#map-locked').hidden=hasPermission('operations-read'); q('#map-content').hidden=!hasPermission('operations-read');
    if(!hasPermission('operations-read')) return;
    const generation=model.generation;
    try { if(!model.config) { const config=await api('/api/v1/map/config',{},true); if(generation!==model.generation) return;model.config=config; } if(generation!==model.generation || state.view!=='map') return;
      q('#map-seed').hidden=!state.auth?.demoMode || !hasPermission('dispatch'); q('#map-privacy-text').textContent=model.config.privacyNotice || 'Location is processed only for municipal incident response. Worker locations require an active shift and explicit revocable consent. Real worker location collection is disabled; demo positions are synthetic.';
      if(!model.map) initializeMap(model.config); model.map.invalidateSize(); await reload(); if(generation===model.generation && state.view==='map') stream();
    } catch(failure) { if(authorizationLost(failure,generation)) return; if(generation===model.generation) error(failure.message); }
  }
  function hide() { model.stream?.abort(); clearTimeout(model.streamRetry); clearTimeout(model.reloadTimer); }
  function reset() { hide(); model.generation++; model.map?.remove(); model.compact?.remove(); model.map=null;model.compact=null;model.config=null;model.features=[];model.selected=null;model.loading=false;model.reloadPending=false;model.dispatching=null;model.decision=null;model.selectionVersion++;model.markers.clear();model.groups=[];model.heat=[]; q('#map-inspector').replaceChildren();q('#map-cases').replaceChildren();q('#map-accessible-items').replaceChildren();q('#map-accessible-count').textContent='0';q('#map-ward-list').replaceChildren();q('#map-ward-info').hidden=true; for(const layer of allowedLayers) q('#map-count-'+layer).textContent='0'; q('#map-locked p').textContent='Incident locations and response-unit details are restricted to your municipality and staff role.';summary('Municipal map data cleared. Sign in with an authorized staff role.');q('#map-content').hidden=true;q('#map-locked').hidden=false; }
  async function attachCase(incident) {
    model.compact?.remove();model.compact=null;
    if(!incident.location || !hasPermission('operations-read')) return;
    const position=[Number(incident.location.latitude),Number(incident.location.longitude)]; if(!position.every(Number.isFinite)) return;
    const generation=model.generation; const section=document.createElement('section');section.className='case-location-context';section.setAttribute('aria-label','Case location context');section.innerHTML=`<div class="case-location-heading"><strong>Incident location · ${esc(incident.wardId || 'Outside configured wards')}</strong><span class="map-badge ${incident.synthetic ? '' : 'warning'}">${incident.synthetic ? 'SYNTHETIC' : 'SHARED · UNVERIFIED'}</span></div><div class="case-location-map" id="case-location-map" role="region" aria-label="Compact incident location map, use arrow keys to pan"></div><p>${incident.synthetic ? 'Synthetic Demo Metro coordinate and ward boundary. No real addresses or worker tracking.' : 'Resident-shared incident location. Municipality and ward are geometric suggestions; an authorized human verifies the location.'}</p>`;
    q('#case-detail .detail-summary')?.after(section);
    try { const config=model.config || await api('/api/v1/map/config',{},true); if(generation!==model.generation || !section.isConnected) return;
      const map=L.map(section.querySelector('.case-location-map'),{zoomControl:false,scrollWheelZoom:false,attributionControl:true});model.compact=map;map.attributionControl.setPrefix(false);map.attributionControl.addAttribution('Configured boundaries · CivicOps');addTiles(map,config,true);
      const wards=model.features.filter(feature=>['wards','municipalities'].includes(feature.properties.layer)); const collection=wards.length ? {features:wards} : await api('/api/v1/map/wards',{},true); if(generation!==model.generation || !section.isConnected) {if(model.compact===map){map.remove();model.compact=null;}return;}
      L.geoJSON(collection.features,{style:()=>({color:'#9acaae',weight:1.5,fillColor:'#3b6957',fillOpacity:.18})}).addTo(map);
      L.marker(position,{keyboard:false,interactive:false,icon:caseIcon(incident)}).addTo(map);map.setView(position,14);
    } catch(failure) { if(authorizationLost(failure,generation)) return; if(section.isConnected) section.querySelector('p').textContent='Location map unavailable: '+failure.message; }
  }
  for(const layer of [...allowedLayers,'heatmap']) q('#map-layer-'+layer).addEventListener('change',()=> { redraw();if(layer==='heatmap') { q('#map-heat-controls').hidden=!enabled('heatmap'); if(enabled('heatmap')) reload(); } });
  q('#map-accessible-filter').addEventListener('change',renderFeatureBrowser);
  q('#map-window').addEventListener('change',()=>enabled('heatmap') ? reload() : redraw());q('#map-heat-metric').addEventListener('change',reload);
  q('#map-refresh').addEventListener('click',reload); q('#map-reset-view').addEventListener('click',()=> {model.selected=null;model.map?.fitBounds(bounds(model.config),{padding:[28,28]});redraw();});
  q('#map-seed').addEventListener('click',async event=> {const generation=model.generation;const button=event.currentTarget;if(button.disabled)return;button.disabled=true;try {await api('/api/v1/map/demo/seed',{method:'POST'},true);await reload();}catch(failure){if(!authorizationLost(failure,generation) && generation===model.generation)error(failure.message);}finally{button.disabled=false;}});
  q('#map-search').addEventListener('submit',async event=> { event.preventDefault();const generation=model.generation;const query=q('#map-case-search').value.trim();const feature=featureList('cases').find(item=>item.properties.referenceNumber.toLowerCase()===query.toLowerCase());if(feature) {await select(feature);if(generation!==model.generation)return;model.map.setView(point(feature),16);} else { try {const incident=await api('/api/dispatch/incidents/'+encodeURIComponent(query),{},true);if(generation!==model.generation)return;if(!incident.location){error('This case has no shared geographic coordinate. Open its workspace for review.');return;}model.map.setView([incident.location.latitude,incident.location.longitude],16);await reload();if(generation!==model.generation)return;const found=featureList('cases').find(item=>item.properties.id===incident.id);if(found) await select(found);else error('The case is outside the current time window. Choose a wider window.');}catch(failure){if(authorizationLost(failure,generation))return;if(generation!==model.generation)return;error(failure.status===404 ? 'No case matches that reference in your municipality.' : failure.message);}} });
  q('#map-open-access').addEventListener('click',()=> {q('#staff-access').open=true;q('#staff-access').scrollIntoView({block:'start'});q('#staff-access summary').focus();});
  q('#map-legend').innerHTML=['Urgent','High','Medium','Low'].map(priority=>`<span>${shape(priority,true)}${priority}</span>`).join('')+'<span>◉ crew</span><span>◇ hazard + radius</span><span>? review · ✓ approved · ↗ assigned · •• active</span><span>Dashed / faded: closed or stale</span>';
  setInterval(()=> {if(state.view==='map') reload();},30000);window.addEventListener('pagehide',hide);window.addEventListener('offline',()=> {hide();connection(false,'Offline · displayed snapshot may be stale');});window.addEventListener('online',()=> {if(state.view==='map')show();});
  return {show,hide,reset,attachCase,reload};
})();
