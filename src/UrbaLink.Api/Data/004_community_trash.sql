ALTER TABLE communities ADD COLUMN deleted_at timestamptz;
ALTER TABLE communities ADD COLUMN deleted_by uuid REFERENCES users(id);
CREATE VIEW active_memberships AS
SELECT m.* FROM memberships m JOIN communities c ON c.id=m.community_id WHERE c.deleted_at IS NULL;
INSERT INTO schema_versions(version) VALUES (4);
