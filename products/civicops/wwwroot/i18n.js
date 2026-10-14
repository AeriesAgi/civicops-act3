'use strict';
window.CivicOpsI18n = (() => {
  const supported = ['en', 'af', 'zu'];
  const resources = {};
  let language = 'en';
  const normalize = value => supported.includes(value) ? value : 'en';
  const text = (key, values = {}, chosen = language) => {
    let message = resources[normalize(chosen)]?.[key] ?? resources.en?.[key] ?? key;
    for (const [name, value] of Object.entries(values)) message = message.replaceAll('{' + name + '}', String(value));
    return message;
  };
  const apply = initial => {
    document.documentElement.lang = language;
    document.querySelector('#resident').lang = language;
    document.querySelectorAll('[data-i18n],[data-i18n-placeholder],[data-i18n-aria-label]').forEach(element => {
      if (!initial && element.hasAttribute('data-i18n-initial')) return;
      const key = element.dataset.i18n;
      const chosen = key?.startsWith('staff.') ? 'en' : language;
      if (key) {
        const message = text(key, {}, chosen);
        // Emergency numbers retain emphasis without interpreting translation strings as HTML.
        if (element.hasAttribute('data-i18n-numbers')) {
          element.replaceChildren(...message.split(/(10111|10177|112)/).map(part => {
            if (!/^(10111|10177|112)$/.test(part)) return document.createTextNode(part);
            const number = document.createElement('strong'); number.textContent = part; return number;
          }));
        } else element.textContent = message;
        element.lang = chosen;
      }
      for (const attribute of ['placeholder', 'aria-label']) {
        const attributeKey = element.getAttribute('data-i18n-' + attribute);
        if (attributeKey) element.setAttribute(attribute, text(attributeKey, {}, attributeKey.startsWith('staff.') ? 'en' : language));
      }
    });
    for (const id of ['ui-language', 'language']) document.querySelector('#' + id).value = language;
  };
  const setLanguage = value => {
    language = normalize(value);
    try { localStorage.setItem('civicops-language', language); } catch { /* Preference storage is optional. */ }
    apply(false);
    dispatchEvent(new CustomEvent('civicops:language', { detail: { language } }));
  };
  const ready = Promise.all(supported.map(async chosen => {
    const response = await fetch('/locales/' + chosen + '.json', { credentials: 'same-origin' });
    if (!response.ok) throw new Error('Language resource unavailable.');
    resources[chosen] = (await response.json()).strings;
  })).then(() => {
    try { language = normalize(localStorage.getItem('civicops-language')); } catch { /* English remains available. */ }
    apply(true);
    for (const id of ['ui-language', 'language']) document.querySelector('#' + id).addEventListener('change', event => setLanguage(event.target.value));
  }).catch(() => {
    language = 'en';
    document.querySelector('#translation-review').textContent = 'Language resources are unavailable. English is shown; reconnect and reload to change language.';
    for (const id of ['ui-language', 'language']) document.querySelector('#' + id).disabled = true;
  });
  return { text, ready, setLanguage, get language() { return language; } };
})();
