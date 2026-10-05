CREATE TABLE IF NOT EXISTS schema_versions (
    version integer PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS users (
    id uuid PRIMARY KEY,
    name varchar(80) NOT NULL,
    email varchar(254) NOT NULL UNIQUE,
    password_hash text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS sessions (
    token_hash char(64) PRIMARY KEY,
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    expires_at timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS sessions_user_id ON sessions(user_id);
CREATE TABLE IF NOT EXISTS communities (
    id uuid PRIMARY KEY,
    name varchar(100) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS memberships (
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    community_id uuid NOT NULL REFERENCES communities(id) ON DELETE CASCADE,
    role varchar(10) NOT NULL CHECK (role IN ('Admin', 'Member')),
    joined_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(user_id, community_id)
);
CREATE TABLE IF NOT EXISTS invitations (
    code_hash char(64) PRIMARY KEY,
    community_id uuid NOT NULL REFERENCES communities(id) ON DELETE CASCADE,
    created_by uuid NOT NULL REFERENCES users(id),
    expires_at timestamptz NOT NULL,
    used_by uuid REFERENCES users(id),
    used_at timestamptz
);
INSERT INTO schema_versions(version) VALUES (1) ON CONFLICT DO NOTHING;
