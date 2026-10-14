'use strict';
const staffRoles = ['resident','agent','dispatcher','supervisor','crew','auditor','admin','training-client'];
const serviceScopes = ['tools:invoke','zendesk:webhook','training:read','training:write','incidents:read','incidents:write','audit:read','integrations:write'];
let adminSnapshot = null;
let adminLoading = false;
let privacyRequests = [];
document.querySelectorAll('.admin-list').forEach(element=>{
  element.tabIndex=0; element.setAttribute('role','region');
  element.setAttribute('aria-label',element.closest('.admin-panel').querySelector('h2').textContent + ' records, scroll for more');
});
function roleChoices(roles, selected = [], prefix = 'role') {
  return roles.map(role => `<label class="check"><input type="checkbox" name="${esc(prefix)}" value="${esc(role)}"${selected.includes(role) ? ' checked' : ''}> ${esc(role)}</label>`).join('');
}
$('#admin-user-roles').insertAdjacentHTML('beforeend',roleChoices(staffRoles,['dispatcher']));
$('#admin-service-scopes').insertAdjacentHTML('beforeend',roleChoices(serviceScopes,['tools:invoke'],'scope'));
function dismissAdminToken() {
  $('#admin-new-token').value = '';
  $('#admin-token-detail').textContent = '';
  $('#admin-token-reveal').hidden = true;
}
function clearStaffData() {
  state.authEpoch++;
  window.CivicOpsMap?.reset();
  window.CivicOpsChannels?.reset();
  dismissAdminToken(); adminSnapshot = null; state.streamAbort?.abort();
  privacyRequests = []; $('#admin-privacy-choice').replaceChildren(); for(const selector of ['#admin-privacy-reference','#admin-privacy-note','#admin-privacy-verification','#admin-privacy-version','#admin-privacy-description','#admin-privacy-email','#admin-privacy-phone']) $(selector).value = ''; $('#admin-privacy-contact').checked = false; $('#admin-privacy-result').textContent = '';
  state.cases = []; state.crews = []; state.catalogue = []; state.selected = null; state.metrics = null; state.lastRefresh = 0;
  for(const selector of ['#queue','#kpis','#outcome-metrics','#audit-rows','#outbox','#admin-metrics','#admin-tenants','#admin-users','#admin-service-accounts','#admin-catalogue','#admin-crews','#admin-workers','#admin-privacy-requests']) $(selector).replaceChildren();
  for(const selector of ['#admin-tenant-json','#admin-new-tenant-json','#admin-catalogue-json','#admin-crew-json','#admin-worker-json']) $(selector).value = '';
  $('#case-detail').innerHTML = `<span class="eyebrow">${staff('staff.dynamic.case_workspace_325c303a')}</span><h2>${staff('staff.dynamic.choose_a_case_203b1ed8')}</h2><p class="hint">${staff('staff.dynamic.sign_in_to_review_work_within_your_municipality_e022832e')}</p>`;
  $('#admin-content').hidden = true; $('#audit-result').textContent = staff('staff.dynamic.sign_in_with_a_role_permitted_to_review_the_audit_d24b925c');
}
async function loadIdentity() {
  const authEpoch = state.authEpoch;
  const identity = await api('/auth/status');
  if(authEpoch !== state.authEpoch) return state.auth;
  state.auth = identity;
  const roles = identity.roles || [];
  const isAdmin = identity.authenticated && hasPermission('admin');
  $('[data-view="admin"]').hidden = !isAdmin;
  $('[data-view="audit"]').hidden = !hasPermission('audit');
  $('[data-view="control"]').hidden = !hasPermission('staff-identity');
  $('[data-view="map"]').hidden = !hasPermission('operations-read');
  $('[data-view="inbox"]').hidden = !hasPermission('operations-read');
  $('[data-view="agencies"]').hidden = !hasPermission('operations-read');
  $('[data-view="integrations"]').hidden = !hasPermission('operations-read');
  $('#control .metrics-card').hidden = !hasPermission('operations-read');
  $('#control .outbox-card').hidden = !hasPermission('operations-read');
  $('#seed').hidden = !identity.demoMode || !hasPermission('dispatch');
  $('#staff-signin').hidden = !identity.oidcConfigured;
  $('#staff-signin').href = localPath('/auth/login') + '?returnUrl=' + encodeURIComponent(municipalityPath + '/#control');
  $('#staff-signout').hidden = !identity.authenticated;
  $('#sso-disabled').hidden = !!identity.oidcConfigured;
  $('#bootstrap-login-form').hidden = false;
  $('#demo-login-form').hidden = !identity.demoMode;
  $('#staff-summary').textContent = identity.authenticated ? `${identity.name || 'Staff'} · ${roles.join(', ')} · ${identity.tenantId}` : staff('staff.dynamic.sign_in_to_your_municipality_79b6edac');
  $('#staff-identity').textContent = identity.authenticated ? `${identity.name || 'Staff'} · ${roles.join(', ')} · municipality ${identity.tenantId}${identity.demoMode ? ' · unverified demo identity' : ''}` : `${identity.oidcConfigured ? identity.provider + ' SSO is available.' : staff('staff.dynamic.no_staff_session_dde3349e')} Municipal access is verified by the server.`;
  const tenants = identity.tenants || (identity.demoMode ? await api('/api/tenants') : []);
  if(authEpoch !== state.authEpoch) return state.auth;
  $('#demo-tenant').innerHTML = tenants.map(tenant => `<option value="${esc(tenant.id)}">${esc(tenant.name)}${tenant.synthetic ? ' · synthetic' : ''}</option>`).join('');
  if(identity.tenantId) $('#demo-tenant').value = identity.tenantId;
  if(roles.length === 1 && staffRoles.includes(roles[0])) $('#demo-role').value = roles[0];
  $('#admin-locked').hidden = !!isAdmin;
  if(!isAdmin) { $('#admin-content').hidden = true; dismissAdminToken(); }
  return identity;
}
async function staffTransition(path, body, message) {
  const previousView = state.view;
  await api(path,{method:'POST',body:JSON.stringify(body)});
  state.token = ''; $('#dispatch-token').value = ''; $('#bootstrap-token').value = '';
  clearStaffData(); await loadIdentity();
  result('#staff-result',message);
  setView(previousView);
  if(hasPermission('operations-read')) startStream();
}
$('#demo-login-form').addEventListener('submit',async event => {
  event.preventDefault(); const button = event.currentTarget.querySelector('button'); if(button.disabled) return; button.disabled = true;
  try { await staffTransition('/auth/demo-login',{role:$('#demo-role').value,tenantId:$('#demo-tenant').value},staff('staff.dynamic.explicit_demo_unverified_role_selected_for_synthetic_mu_4853452a')); }
  catch(error) { result('#staff-result',error.message,false); }
  finally { button.disabled = false; }
});
$('#bootstrap-login-form').addEventListener('submit',async event => {
  event.preventDefault(); const button = event.currentTarget.querySelector('button'); if(button.disabled) return; button.disabled = true;
  const token = $('#bootstrap-token').value.trim(); $('#bootstrap-token').value = '';
  try { await staffTransition('/auth/bootstrap-login',{token},staff('staff.dynamic.staff_session_connected_with_server_verified_municipal__1463f382')); }
  catch(error) { result('#staff-result',error.message,false); }
  finally { button.disabled = false; }
});
$('#staff-signout').addEventListener('click',async event => {
  const button = event.currentTarget; button.disabled = true;
  if(state.auth?.oidcSession) {
    // A normal browser navigation completes the provider's federated sign-out redirect.
    clearStaffData(); state.token = '';
    const form=document.createElement('form'); form.method='POST'; form.action=localPath('/auth/logout');
    const csrf=document.createElement('input'); csrf.type='hidden'; csrf.name='__RequestVerificationToken'; csrf.value=state.auth.csrfToken;
    form.append(csrf); document.body.append(form); form.submit(); return;
  }
  try { await staffTransition('/auth/logout',{},staff('staff.dynamic.staff_session_ended_a55fd5c1')); setView('resident'); location.hash = 'resident'; }
  catch(error) { result('#staff-result',error.message,false); }
  finally { button.disabled = false; }
});
$('#admin-open-access').addEventListener('click',() => { $('#staff-access').open = true; $('#staff-access').scrollIntoView({block:'start'}); $('#staff-access summary').focus(); });
$('#admin-token-dismiss').addEventListener('click',dismissAdminToken);
$('#admin-refresh').addEventListener('click',async () => { await loadAdmin(true); $('#admin-refresh').focus({preventScroll:true}); });
function showAdminToken(response, title, detail = '') {
  if(!response.token) throw new Error(staff('staff.dynamic.the_server_did_not_return_a_new_token_refresh_the_accou_b1269efa'));
  $('#admin-token-title').textContent = title;
  $('#admin-new-token').value = response.token;
  $('#admin-token-detail').textContent = detail;
  $('#admin-token-reveal').hidden = false;
  $('#admin-token-reveal').scrollIntoView({block:'center'});
  $('#admin-new-token').focus(); $('#admin-new-token').select();
}
function compactList(items, render, empty) { return items.length ? items.map(render).join('') : `<p class="hint admin-empty">${esc(empty)}</p>`; }
function renderAdminTenants(snapshot) {
  const tenants=snapshot.tenants.slice().sort((a,b)=>Number(b.id===snapshot.tenant.id)-Number(a.id===snapshot.tenant.id));
  $('#admin-tenants').innerHTML = compactList(tenants,tenant => `<article class="admin-record"><div class="admin-record-head"><strong>${esc(tenant.name)}</strong><span class="pill ${tenant.synthetic ? 'warned' : 'muted'}">${tenant.id===snapshot.tenant.id ? staff('staff.dynamic.current_706d3c86') : ''}${tenant.synthetic ? 'SYNTHETIC' : 'MUNICIPALITY'}</span></div><p>${esc(tenant.id)} · /m/${esc(tenant.slug)}</p><p class="hint">${esc(tenant.departments.join(' · '))}</p><p class="hint">${esc(tenant.localeDefaults.language)} · ${esc(tenant.localeDefaults.timeZone)} · ${esc(tenant.serviceArea)}</p></article>`,staff('staff.dynamic.no_municipality_configuration_is_available_04dadf08'));
  $('#admin-tenant-json').value = JSON.stringify(snapshot.tenant,null,2);
  $('#admin-create-tenant').hidden = snapshot.tenant.id !== 'default';
  $('#admin-new-tenant-json').value = JSON.stringify({id:'synthetic-new',name:staff('staff.dynamic.synthetic_new_municipality_7f9f8184'),slug:'synthetic-new',departments:[staff('staff.dynamic.public_works_b867ebd7')],localeDefaults:{language:'en',timeZone:'Africa/Johannesburg'},serviceArea:staff('staff.dynamic.synthetic_service_area_f8a4179f'),synthetic:true,residentHost:null,version:1},null,2);
}
function renderAdminUsers(users) {
  $('#admin-users').innerHTML = compactList(users,user => `<article class="admin-record"><div class="admin-record-head"><strong>${esc(user.displayName)}</strong><span class="pill ${user.revokedAt ? 'expired' : 'eligible'}">${user.revokedAt ? 'REVOKED' : 'ACTIVE'}</span></div><p>${esc(user.subject)}${user.crewId ? ' · crew ' + esc(user.crewId) : ''}</p><div class="admin-badges">${user.roles.map(role=>`<span class="pill muted">${esc(role)}</span>`).join('')}</div>${user.revokedAt ? '' : `<details><summary>${staff('staff.dynamic.manage_staff_access_ee7ab4d3')}</summary><form data-edit-user="${esc(user.id)}"><label>${staff('staff.dynamic.display_name_2b7f6a84')}<input name="displayName" required maxlength="160" value="${esc(user.displayName)}"></label><label>${staff('staff.dynamic.crew_id_8cfaa81e')}<input name="crewId" maxlength="80" value="${esc(user.crewId || '')}"></label><fieldset class="role-grid"><legend>${staff('staff.dynamic.assigned_roles_423dc5d9')}</legend>${roleChoices(staffRoles,user.roles)}</fieldset><div class="actions"><button class="secondary" type="submit">${staff('staff.dynamic.save_roles_1a0378a9')}</button><button class="secondary" type="button" data-user-rotate="${esc(user.id)}">${staff('staff.dynamic.rotate_token_4ade7882')}</button><button class="secondary danger-action" type="button" data-user-revoke="${esc(user.id)}">${staff('staff.dynamic.revoke_access_ab292ddb')}</button></div></form></details>`}</article>`,staff('staff.dynamic.no_local_staff_users_sso_staff_sign_in_through_the_iden_608d8058'));
  $('#admin-users').querySelectorAll('[data-edit-user]').forEach(form => form.addEventListener('submit',async event => {
    event.preventDefault(); await adminAction(form,'#admin-user-result',async () => {
      const fields = new FormData(form);
      await api('/api/admin/users/' + encodeURIComponent(form.dataset.editUser),{method:'PUT',body:JSON.stringify({displayName:fields.get('displayName'),crewId:fields.get('crewId') || null,roles:fields.getAll('role')})},true);
      return staff('staff.dynamic.staff_role_assignment_saved_and_audited_2215be88');
    });
  }));
  $('#admin-users').querySelectorAll('[data-user-rotate],[data-user-revoke]').forEach(button => button.addEventListener('click',async () => {
    const id = button.dataset.userRotate || button.dataset.userRevoke; const rotate = !!button.dataset.userRotate;
    await adminAction(button,'#admin-user-result',async () => {
      const response = await api('/api/admin/users/' + encodeURIComponent(id) + (rotate ? '/rotate' : '/revoke'),{method:'POST'},true);
      if(rotate) showAdminToken(response,staff('staff.dynamic.rotated_staff_bootstrap_token_b6e9e769'),staff('staff.dynamic.the_previous_bootstrap_token_has_been_replaced_a2d93b05'));
      return rotate ? staff('staff.dynamic.staff_token_rotated_and_audited_2e9cc28f') : staff('staff.dynamic.staff_access_revoked_and_audited_61b93bec');
    });
  }));
}
function renderAdminAccounts(accounts) {
  $('#admin-service-accounts').innerHTML = compactList(accounts,account => {
    const expired = Date.parse(account.expiresAt) <= Date.now();
    return `<article class="admin-record"><div class="admin-record-head"><strong>${esc(account.name)}</strong><span class="pill ${account.revokedAt || expired ? 'expired' : 'eligible'}">${account.revokedAt ? 'REVOKED' : expired ? 'EXPIRED' : 'ACTIVE'}</span></div><p class="hint">Municipality ${esc(account.tenantId)} · expires ${esc(date(account.expiresAt))}</p><div class="admin-badges">${account.scopes.map(scope=>`<span class="pill muted">${esc(scope)}</span>`).join('')}</div>${account.revokedAt ? '' : `<div class="actions admin-record-actions"><button class="secondary" type="button" data-account-rotate="${esc(account.id)}">${staff('staff.dynamic.rotate_5_min_overlap_3f2520a1')}</button><button class="secondary danger-action" type="button" data-account-revoke="${esc(account.id)}">${staff('staff.dynamic.revoke_token_14683b37')}</button></div>`}</article>`;
  },staff('staff.dynamic.no_managed_service_accounts_create_a_scoped_account_for_83b1de4d'));
  $('#admin-service-accounts').querySelectorAll('[data-account-rotate],[data-account-revoke]').forEach(button => button.addEventListener('click',async () => {
    const id = button.dataset.accountRotate || button.dataset.accountRevoke; const rotate = !!button.dataset.accountRotate;
    await adminAction(button,'#admin-service-result',async () => {
      const response = await api('/api/admin/service-accounts/' + encodeURIComponent(id) + (rotate ? '/rotate' : '/revoke'),{method:'POST',...(rotate ? {body:JSON.stringify({overlapMinutes:5})} : {})},true);
      if(rotate) showAdminToken(response,staff('staff.dynamic.rotated_service_account_token_834b90a9'),staff('staff.dynamic.the_previous_token_remains_valid_for_the_configured_fiv_3d418df6'));
      return rotate ? staff('staff.dynamic.token_rotated_and_audited_copy_the_new_token_now_c337c6ce') : staff('staff.dynamic.service_account_revoked_and_audited_f16fe34d');
    });
  }));
}
function renderAdminCatalogue(catalogue) {
  $('#admin-catalogue-version').textContent = staff('staff.dynamic.version_f1df20b3') + catalogue.version;
  $('#admin-catalogue').innerHTML = catalogue.workOrderTypes.map(type => `<article class="admin-record"><div class="admin-record-head"><strong>${esc(type.title)}</strong><span class="pill ${['high','critical'].includes(type.hazardClass) ? 'high' : 'muted'}">${esc(type.hazardClass)} hazard</span></div><p>${esc(type.code)} · ${esc(type.department)}</p><p class="hint">${type.requiredSkills.length} skill requirements · ${type.requiredCertifications.length} certification requirements · review ${type.slaTargetMinutes} min</p></article>`).join('');
  $('#admin-catalogue-json').value = JSON.stringify(catalogue,null,2);
}
function populateWorkforceEditor(kind, records) {
  const current = $('#admin-' + kind + '-choice').value;
  $('#admin-' + kind + '-choice').innerHTML = '<option value="">Create a new ' + kind + '</option>' + records.map(record=>`<option value="${esc(record.id)}">${esc(record.name || record.displayName)}</option>`).join('');
  $('#admin-' + kind + '-choice').value = records.some(record=>record.id===current) ? current : records[0]?.id || '';
  updateWorkforceEditor(kind);
}
function updateWorkforceEditor(kind) {
  if(!adminSnapshot) return;
  const records = kind === 'crew' ? adminSnapshot.crews : adminSnapshot.workers;
  const record = records.find(item=>item.id===$('#admin-' + kind + '-choice').value);
  const template = kind === 'crew' ? {id:'synthetic-new-crew',name:staff('staff.dynamic.synthetic_new_crew_f8ac2145'),department:adminSnapshot.tenant.departments[0],capability:staff('staff.dynamic.synthetic_municipal_response_47093d75'),memberIds:[],shift:'day',availability:'available',tenantId:adminSnapshot.tenant.id} : {id:'synthetic-new-worker',displayName:staff('staff.dynamic.synthetic_new_worker_31c69700'),crewId:adminSnapshot.crews[0]?.id || '',skills:[],certifications:[],tenantId:adminSnapshot.tenant.id};
  $('#admin-' + kind + '-json').value = JSON.stringify(record || template,null,2);
}
function renderAdminWorkforce(snapshot) {
  $('#admin-crews').innerHTML = compactList(snapshot.crews,crew=>`<article class="admin-record"><div class="admin-record-head"><strong>${esc(crew.name)}</strong><span class="pill muted">${esc(crew.availability)}</span></div><p>${esc(crew.department)} · ${esc(crew.shift)} shift</p><p class="hint">${esc(crew.capability)} · ${(crew.memberIds || []).length} members</p></article>`,staff('staff.dynamic.no_municipal_crews_c7ca3ff6'));
  $('#admin-workers').innerHTML = compactList(snapshot.workers,worker=>`<article class="admin-record"><div class="admin-record-head"><strong>${esc(worker.displayName)}</strong><span class="pill muted">${esc(worker.crewId)}</span></div><p class="hint">${worker.skills.map(skill=>`${esc(skill.code)} · L${skill.level}`).join(' · ') || staff('staff.dynamic.no_recorded_skills_8ef4b77d')}</p><div class="admin-badges">${worker.certifications.map(cert=>`<span class="pill ${Date.parse(cert.expiresAt)<=Date.now() ? 'expired' : Date.parse(cert.issuedAt)>Date.now() ? 'warned' : 'valid'}" title="${esc(cert.issuer)} · ${esc(cert.evidenceRef)}">${esc(cert.code)} · ${Date.parse(cert.expiresAt)<=Date.now() ? 'EXPIRED' : Date.parse(cert.issuedAt)>Date.now() ? staff('staff.dynamic.not_yet_valid_61159916') : 'VALID'}</span>`).join('') || `<span class="hint">${staff('staff.dynamic.no_recorded_certifications_d5c79c7b')}</span>`}</div></article>`,staff('staff.dynamic.no_municipal_workers_2960b18a'));
  populateWorkforceEditor('crew',snapshot.crews); populateWorkforceEditor('worker',snapshot.workers);
}
async function loadAdmin(force = false) {
  if(adminLoading) return;
  if(!state.auth?.authenticated || !hasPermission('admin')) { $('#admin-locked').hidden = false; $('#admin-content').hidden = true; return; }
  if(adminSnapshot && !force) return;
  adminLoading = true; $('#admin-refresh').disabled = true;
  const identity = state.auth;
  try {
    const [snapshot,accounts,requests] = await Promise.all([api('/api/admin/overview',{},true),api('/api/admin/service-accounts',{},true),api('/api/admin/privacy/requests',{},true)]);
    if(identity !== state.auth) return;
    adminSnapshot = snapshot;
    $('#admin-tenant').textContent = snapshot.tenant.name + (snapshot.tenant.synthetic ? ' · SYNTHETIC' : '');
    $('#admin-metrics').innerHTML = kpi('Municipalities',snapshot.tenants.length,staff('staff.dynamic.visible_tenant_configuration_dc140d78')) + kpi(staff('staff.dynamic.bootstrap_users_13d9ee32'),snapshot.users.filter(user=>!user.revokedAt).length,staff('staff.dynamic.active_local_staff_assignments_a0eac744')) + kpi(staff('staff.dynamic.service_accounts_1642d9e2'),accounts.filter(account=>!account.revokedAt && Date.parse(account.expiresAt)>Date.now()).length,staff('staff.dynamic.active_scoped_integrations_91245c2b')) + kpi(staff('staff.dynamic.qualified_workforce_85b6198a'),snapshot.workers.length,`${snapshot.crews.length} municipal crews`) + kpi(staff('staff.dynamic.catalogue_version_0654f3a4'),snapshot.catalogue.version,`${snapshot.catalogue.workOrderTypes.length} validated work-order types`);
    renderAdminTenants(snapshot); renderAdminUsers(snapshot.users); renderAdminAccounts(accounts); renderAdminCatalogue(snapshot.catalogue); renderAdminWorkforce(snapshot); renderPrivacyRequests(requests);
    if(!$('#admin-service-expiry').value) $('#admin-service-expiry').value = new Date(Date.now()+30*86400000-new Date().getTimezoneOffset()*60000).toISOString().slice(0,16);
    $('#admin-locked').hidden = true; $('#admin-error').hidden = true; $('#admin-content').hidden = false;
    window.CivicOpsChannels?.loadTemplates();
  } catch(error) { if(identity===state.auth) { $('#admin-content').hidden = true; result('#admin-error',error.message,false); } }
  finally { adminLoading = false; $('#admin-refresh').disabled = false; if(identity!==state.auth && state.view==='admin' && hasPermission('admin')) loadAdmin(true); }
}
async function adminAction(element, output, action) {
  const buttons = element.matches('button') ? [element] : [...element.querySelectorAll('button')];
  if(buttons.some(button=>button.disabled)) return;
  buttons.forEach(button=>button.disabled=true);
  try { const message = await action(); await loadAdmin(true); result(output,message); }
  catch(error) { result(output,error.message,false); }
  finally { buttons.forEach(button=>button.disabled=false); }
}
function editorJson(selector) {
  try { const value = JSON.parse($(selector).value); if(!value || Array.isArray(value) || typeof value !== 'object') throw new Error(); return value; }
  catch { throw new Error(staff('staff.dynamic.enter_a_valid_json_object_your_changes_are_kept_in_the__43cc8d51')); }
}
$('#admin-user-form').addEventListener('submit',async event => {
  event.preventDefault(); await adminAction(event.currentTarget,'#admin-user-result',async () => {
    const response = await api('/api/admin/users',{method:'POST',body:JSON.stringify({subject:$('#admin-user-subject').value.trim(),displayName:$('#admin-user-name').value.trim(),crewId:$('#admin-user-crew').value.trim() || null,roles:[...$('#admin-user-roles').querySelectorAll('input:checked')].map(input=>input.value)})},true);
    showAdminToken(response,staff('staff.dynamic.new_staff_bootstrap_token_57559c9c'),staff('staff.dynamic.supply_the_token_securely_to_this_synthetic_staff_user_09165c47'));
    return staff('staff.dynamic.staff_user_created_and_audited_copy_the_access_token_no_51efc384');
  });
});
$('#admin-service-form').addEventListener('submit',async event => {
  event.preventDefault(); await adminAction(event.currentTarget,'#admin-service-result',async () => {
    const response = await api('/api/admin/service-accounts',{method:'POST',body:JSON.stringify({name:$('#admin-service-name').value.trim(),scopes:[...$('#admin-service-scopes').querySelectorAll('input:checked')].map(input=>input.value),expiresAt:new Date($('#admin-service-expiry').value).toISOString()})},true);
    showAdminToken(response,staff('staff.dynamic.new_service_account_token_b4557d9b'),staff('staff.dynamic.use_this_municipality_bound_token_only_for_the_selected_813eab1a'));
    return staff('staff.dynamic.service_account_created_and_audited_copy_the_token_now_bb8086bd');
  });
});
$('#admin-tenant-form').addEventListener('submit',async event => {
  event.preventDefault(); await adminAction(event.currentTarget,'#admin-tenant-result',async () => {
    await api('/api/admin/tenants/' + encodeURIComponent(adminSnapshot.tenant.id),{method:'PUT',body:JSON.stringify(editorJson('#admin-tenant-json'))},true);
    return staff('staff.dynamic.municipality_settings_validated_saved_and_audited_e152f4cf');
  });
});
$('#admin-new-tenant-form').addEventListener('submit',async event => {
  event.preventDefault(); await adminAction(event.currentTarget,'#admin-new-tenant-result',async () => {
    await api('/api/admin/tenants',{method:'POST',body:JSON.stringify(editorJson('#admin-new-tenant-json'))},true);
    return staff('staff.dynamic.municipality_provisioned_and_audited_operational_data_r_5710a1d1');
  });
});
$('#admin-catalogue-form').addEventListener('submit',async event => {
  event.preventDefault(); await adminAction(event.currentTarget,'#admin-catalogue-result',async () => {
    await api('/api/admin/catalogue',{method:'PUT',body:JSON.stringify({expectedVersion:adminSnapshot.catalogue.version,catalogue:editorJson('#admin-catalogue-json')})},true);
    return staff('staff.dynamic.new_catalogue_version_validated_saved_and_audited_5c5f8671');
  });
});
for(const kind of ['crew','worker']) {
  $('#admin-' + kind + '-choice').addEventListener('change',()=>updateWorkforceEditor(kind));
  $('#admin-' + kind + '-form').addEventListener('submit',async event => {
    event.preventDefault(); await adminAction(event.currentTarget,'#admin-' + kind + '-result',async () => {
      const record = editorJson('#admin-' + kind + '-json');
      if(typeof record.id !== 'string' || !record.id.trim()) throw new Error(staff('staff.dynamic.the_record_requires_an_id_3b414180'));
      await api('/api/admin/' + (kind === 'crew' ? 'crews' : 'workers') + '/' + encodeURIComponent(record.id),{method:'PUT',body:JSON.stringify(record)},true);
      return kind === 'crew' ? staff('staff.dynamic.crew_validated_saved_and_audited_7e2b3893') : staff('staff.dynamic.worker_qualifications_validated_saved_and_audited_977b30c8');
    });
  });
}
window.addEventListener('pagehide',dismissAdminToken);

function renderPrivacyRequests(requests) {
  privacyRequests = requests;
  $('#admin-privacy-requests').innerHTML = compactList(requests,request => `<article class="admin-record"><div class="admin-record-head"><strong>${esc(request.referenceNumber)} · ${esc(request.action)}</strong><span class="pill muted">${esc(request.status)}</span></div><p class="hint">Requested ${esc(date(request.requestedAt))}${request.verifiedAt ? ' · identity verified ' + esc(date(request.verifiedAt)) : ''}</p></article>`,staff('staff.dynamic.no_recorded_data_subject_requests_375a3c6a'));
  const previous = $('#admin-privacy-choice').value;
  const open = requests.filter(request=>request.status!=='fulfilled');
  $('#admin-privacy-choice').innerHTML = `<option value="">${staff('staff.dynamic.choose_a_request_e26d36a2')}</option>` + open.map(request=>`<option value="${esc(request.id)}">${esc(request.referenceNumber)} · ${esc(request.action)} · ${esc(request.status)}</option>`).join('');
  $('#admin-privacy-choice').value = open.some(request=>request.id===previous) ? previous : open.at(-1)?.id || '';
}
$('#admin-privacy-create').addEventListener('submit',async event => {
  event.preventDefault(); await adminAction(event.currentTarget,'#admin-privacy-result',async () => {
    await api('/api/admin/privacy/requests',{method:'POST',body:JSON.stringify({referenceNumber:$('#admin-privacy-reference').value.trim(),action:$('#admin-privacy-action').value,requestNote:$('#admin-privacy-note').value.trim()})},true);
    return staff('staff.dynamic.request_recorded_verify_the_requester_identity_before_f_093e5d63');
  });
});
$('#admin-privacy-verify').addEventListener('submit',async event => {
  event.preventDefault(); await adminAction(event.currentTarget,'#admin-privacy-result',async () => {
    const id = $('#admin-privacy-choice').value; if(!id) throw new Error(staff('staff.dynamic.choose_a_recorded_request_70f3eabd'));
    await api('/api/admin/privacy/requests/' + encodeURIComponent(id) + '/verify',{method:'POST',body:JSON.stringify({verificationNote:$('#admin-privacy-verification').value.trim()})},true);
    $('#admin-privacy-verification').value = '';
    return staff('staff.dynamic.identity_verification_note_recorded_and_audited_the_ver_6096d88e');
  });
});
$('#admin-privacy-fulfil').addEventListener('submit',async event => {
  event.preventDefault(); await adminAction(event.currentTarget,'#admin-privacy-result',async () => {
    const id = $('#admin-privacy-choice').value; const request = privacyRequests.find(record=>record.id===id);
    if(!request) throw new Error(staff('staff.dynamic.choose_a_recorded_request_70f3eabd'));
    const body = {expectedVersion:$('#admin-privacy-version').value ? Number($('#admin-privacy-version').value) : null,description:$('#admin-privacy-description').value.trim() || null,contactEmail:$('#admin-privacy-email').value.trim() || null,contactPhone:$('#admin-privacy-phone').value.trim() || null,consentToContact:$('#admin-privacy-contact').checked};
    const response = await api('/api/admin/privacy/requests/' + encodeURIComponent(id) + '/fulfil',{method:'POST',body:JSON.stringify(body)},true);
    if(request.action==='access') {
      const url = URL.createObjectURL(new Blob([JSON.stringify(response,null,2)],{type:'application/json'}));
      const link = document.createElement('a'); link.href = url; link.download = 'civicops-privacy-export.json'; link.hidden = true; document.body.append(link); link.click(); link.remove(); setTimeout(()=>URL.revokeObjectURL(url),1000);
    }
    for(const selector of ['#admin-privacy-version','#admin-privacy-description','#admin-privacy-email','#admin-privacy-phone']) $(selector).value = '';
    $('#admin-privacy-contact').checked = false;
    return request.action === 'access' ? staff('staff.dynamic.verified_local_access_export_downloaded_and_audited_d33984da') : staff('staff.dynamic.verified_request_fulfilled_and_audited_provider_records_0f90356b');
  });
});
