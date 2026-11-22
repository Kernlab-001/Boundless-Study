// Handles blocked page display and interaction
(function () {
  'use strict';

  document.addEventListener('DOMContentLoaded', function () {
    var params = new URLSearchParams(window.location.search);
    var domain = params.get('domain');
    var sessionId = params.get('session');
    var targetDisplay = domain || '';
    var domainBox = document.getElementById('domainBox');
    var domainText = document.getElementById('domainText');

    if (targetDisplay && domainBox && domainText) {
      domainText.textContent = targetDisplay;
      domainBox.style.display = 'block';
    }

    var goBackBtn = document.getElementById('goBackBtn');
    if (goBackBtn) {
      goBackBtn.addEventListener('click', function () {
        if (window.history.length > 1) {
          window.history.back();
        } else {
          window.close();
        }
      });
    }

    // The redirect contains only the configured blocked domain, never the URL the
    // student attempted to visit. The service worker validates both values against
    // the current Agent policy before recording a violation.
    if (domain && sessionId && typeof chrome !== 'undefined' && chrome.runtime) {
      chrome.runtime.sendMessage({
        type: 'blockedNavigation',
        domain: domain,
        sessionId: sessionId
      });
    }
  });
})();
