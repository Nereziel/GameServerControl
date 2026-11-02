import fs from 'node:fs';

const auth = fs.readFileSync('src/GameServerControl.Agent/Auth/TokenAuthHandler.cs', 'utf8');
const core = fs.readFileSync('src/GameServerControl.Agent/WebUi/core.js', 'utf8');
const cloudAdapter = fs.readFileSync('src/GameServerControl.Cloud/static/cloud-adapter.js', 'utf8');
const security = fs.readFileSync('SECURITY.md', 'utf8');
const throttlePath = 'src/GameServerControl.Agent/Auth/AuthenticationFailureThrottle.cs';
const throttle = fs.existsSync(throttlePath) ? fs.readFileSync(throttlePath, 'utf8') : '';

function requireToken(source, token, message) {
  if (!source.includes(token)) throw new Error(`${message}: missing ${token}`);
}

function rejectToken(source, token, message) {
  if (source.includes(token)) throw new Error(`${message}: unexpected ${token}`);
}

// Finding 4: bearer credentials in query strings are permitted only for the SignalR status hub.
requireToken(auth, 'Request.Path.StartsWithSegments("/hubs/status")', 'Query-string bearer tokens must be scoped to the SignalR status hub');
requireToken(auth, 'Request.Query.TryGetValue("access_token"', 'SignalR query-token compatibility must remain available');

// Finding 5: browser credentials must not survive the browser session.
requireToken(core, 'sessionStorage', 'Direct Web UI credentials must use session-scoped storage');
rejectToken(core, 'localStorage', 'Direct Web UI must not persist bearer credentials in localStorage');
requireToken(cloudAdapter, 'sessionStorage', 'Cloud adapter connection state must use session-scoped storage');
rejectToken(cloudAdapter, 'localStorage', 'Cloud adapter must not persist connection state in localStorage');

// Finding 6: repeated authentication failures must be blocked before another credential lookup.
requireToken(auth, 'AuthenticationFailureThrottle.Shared', 'Authentication handler must use the shared failure throttle');
requireToken(auth, 'HandleChallengeAsync', 'Throttled authentication challenges must return a distinct response');
requireToken(auth, 'StatusCodes.Status429TooManyRequests', 'Throttled authentication challenges must return HTTP 429');
requireToken(auth, 'Retry-After', 'Throttled authentication challenges must tell clients when to retry');
requireToken(throttle, 'MaxTrackedClients', 'Throttle state must have an explicit memory bound');
requireToken(throttle, 'TryGetRetryAfter', 'Throttle must block before another credential lookup');
requireToken(throttle, 'RecordFailure', 'Throttle must track failed authentication attempts');
requireToken(throttle, 'RecordSuccess', 'Successful authentication must clear failure state');

requireToken(security, 'sessionStorage', 'Security documentation must describe session-scoped browser credentials');
requireToken(security, 'authentication-failure throttling', 'Security documentation must describe built-in authentication-failure throttling');
rejectToken(security, 'There is no built-in authentication-failure rate limiter yet', 'Resolved rate-limit limitation must be removed');

console.log('Audit fixes 4-6 contract OK.');
