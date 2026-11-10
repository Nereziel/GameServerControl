-- Cloudflare D1 stores encrypted (AES-256-GCM) Agent credentials.
-- The encryption key is a separate Worker secret, never stored in D1.
CREATE TABLE IF NOT EXISTS registered_hosts (
  id TEXT PRIMARY KEY NOT NULL CHECK(length(id) BETWEEN 1 AND 40),
  name TEXT NOT NULL CHECK(length(name) BETWEEN 1 AND 80),
  admin_secret TEXT NOT NULL,
  readonly_secret TEXT NOT NULL,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
