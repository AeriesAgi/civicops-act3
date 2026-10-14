'use strict';
// Landing calls-to-action: open the staff access panel when a visitor chooses to try a staff role.
document.querySelectorAll('[data-open-staff]').forEach(link => link.addEventListener('click', () => {
  const panel = document.getElementById('staff-access');
  if(panel) panel.open = true;
}));
