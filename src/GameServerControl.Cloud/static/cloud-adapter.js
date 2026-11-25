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
    boot = async function (identity) {
      CFG = cloudCfg;
      await localBoot(identity);
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

  // The shared init handles Access authentication and the retry state directly.
})();
