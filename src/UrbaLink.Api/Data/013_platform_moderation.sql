ALTER TABLE communities ADD COLUMN platform_state text NOT NULL DEFAULT 'Active' CHECK(platform_state IN ('Pending','Active','Suspended'));
ALTER TABLE communities ALTER COLUMN platform_state SET DEFAULT 'Pending';
ALTER TABLE users ADD COLUMN is_suspended boolean NOT NULL DEFAULT false;
ALTER TABLE memberships ADD COLUMN is_blocked boolean NOT NULL DEFAULT false;
CREATE OR REPLACE VIEW active_memberships AS
SELECT m.user_id,m.community_id,m.role,m.joined_at FROM memberships m
JOIN communities c ON c.id=m.community_id JOIN users u ON u.id=m.user_id
WHERE c.deleted_at IS NULL AND c.platform_state='Active' AND NOT m.is_blocked AND NOT u.is_suspended;
CREATE TABLE platform_sessions(token_hash char(64) PRIMARY KEY,user_id uuid NOT NULL REFERENCES users(id),expires_at timestamptz NOT NULL);
CREATE TABLE platform_totp_usage(user_id uuid PRIMARY KEY REFERENCES users(id),last_step bigint NOT NULL);
CREATE TABLE platform_audit(
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,actor_id uuid NOT NULL REFERENCES users(id),
 action text NOT NULL,target text NOT NULL,reason varchar(2000) NOT NULL DEFAULT '',created_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE moderation_reports(
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,community_id uuid NOT NULL REFERENCES communities(id),
 reporter_id uuid NOT NULL REFERENCES users(id),request_id uuid NOT NULL,kind text NOT NULL,
 target_id bigint NOT NULL,reason varchar(2000) NOT NULL,snapshot jsonb NOT NULL,
 status text NOT NULL DEFAULT 'Open' CHECK(status IN ('Open','Resolved','Dismissed')),
 resolution varchar(2000) NOT NULL DEFAULT '',reviewed_by uuid REFERENCES users(id),
 created_at timestamptz NOT NULL DEFAULT now(),reviewed_at timestamptz,
 UNIQUE(reporter_id,request_id));
CREATE INDEX reports_queue ON moderation_reports(status,id DESC);
INSERT INTO schema_versions(version) VALUES(13);
