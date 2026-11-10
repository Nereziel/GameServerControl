(() => {
  const cloudCfg = {
    url: location.origin.replace(/\/+$/, ''),
    token: 'cloud-gateway'
  };

  window.GSC_CLOUD_MODE = true;
  try {
    sessionStorage.setItem('gsc.connection.v2', JSON.stringify(cloudCfg));
    if (!sessionStorage.getItem('gsc.cloud.multi-host.initialized')) {
      sessionStorage.setItem('gsc.activeHost.v1', 'all');
      sessionStorage.setItem('gsc.cloud.multi-host.initialized', '1');
    }
  } catch {}

  if (typeof boot === 'function') {
    const localBoot = boot;
    boot = async function () {
      CFG = cloudCfg;
      await localBoot();
      const label = document.querySelector('#agentLabel');
      if (label) label.textContent = '· Cloudflare remote';
    };
  }

  if (typeof logout === 'function') {
    logout = function () {
      try { sessionStorage.removeItem('gsc.connection.v2'); } catch {}
      location.assign('/cdn-cgi/access/logout');
    };
  }

  if (typeof login === 'function') {
    login = function () {
      location.reload();
    };
  }

  window.addEventListener('DOMContentLoaded', () => {
    setTimeout(() => {
      const loginBox = document.querySelector('#login');
      const app = document.querySelector('#app');
      if (!loginBox || loginBox.classList.contains('hidden') || (app && !app.classList.contains('hidden'))) return;

      loginBox.innerHTML = `
        <h1>GameServerControl</h1>
        <div class="muted">Cloud gateway could not reach the Agent.</div>
        <div class="callout" style="margin-top:16px">Check Cloudflare Access, the VPS HTTPS gateway and the host registry configuration.</div>
        <button id="cloudRetryBtn" class="btn primary" style="width:100%;margin-top:12px">Retry</button>`;
      document.querySelector('#cloudRetryBtn')?.addEventListener('click', () => location.reload());
    }, 1200);
  });
})();
