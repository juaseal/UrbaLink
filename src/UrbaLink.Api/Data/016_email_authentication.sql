-- Email is the login identifier again. Keep legacy username values as historical data.
-- Existing accounts without email must supply one; never invent an address for them.
DROP INDEX users_username_unique;
ALTER TABLE users ALTER COLUMN username DROP NOT NULL;
INSERT INTO schema_versions(version) VALUES (16) ON CONFLICT DO NOTHING;
