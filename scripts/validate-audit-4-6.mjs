import fs from 'node:fs';

const auth = fs.readFileSync('src/GameServerControl.Agent/Auth/TokenAuthHandler.cs', 'utf8');
const core = fs.readFileSync('src/GameServerControl.Agent/WebUi/core.js', 'utf8');
const cloudAdapter = fs.readFileSync('src/GameServerControl.Cloud/static/cloud-adapter.js', 'utf8');
const program = fs.readFileSync('src/GameServerControl.Agent/Program.cs', 'utf8');
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

// Finding 6: repeated authentication failures must be throttled before authentication runs.
requireToken(program, 'AddSingleton<AuthenticationFailureThrottle>()', 'Authentication failure throttle must be registered');
requireToken(program, 'UseMiddleware<AuthenticationFailureThrottleMiddleware>()', 'Authentication failure throttle middleware must be enabled');
const throttleIndex = program.indexOf('UseMiddleware<AuthenticationFailureThrottleMiddleware>()');
const authIndex = program.indexOf('UseAuthentication()');
if (throttleIndex < 0 || authIndex < 0 || throttleIndex > authIndex)
  throw new Error('Authentication failure throttle middleware must run before UseAuthentication().');

requireToken(throttle, 'StatusCodes.Status429TooManyRequests', 'Throttle must return HTTP 429 while a client is blocked');
requireToken(throttle, 'StatusCodes.Status401Unauthorized', 'Throttle must count authentication failures, not authorization denials');
requireToken(throttle, 'Retry-After', 'Throttle must tell clients when to retry');
requireToken(throttle, 'MaxTrackedClients', 'Throttle state must have an explicit memory bound');

requireToken(security, 'sessionStorage', 'Security documentation must describe session-scoped browser credentials');
requireToken(security, 'authentication-failure throttling', 'Security documentation must describe built-in authentication-failure throttling');
rejectToken(security, 'There is no built-in authentication-failure rate limiter yet', 'Resolved rate-limit limitation must be removed');

console.log('Audit fixes 4-6 contract OK.');
