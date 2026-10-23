ALTER TABLE invitations ADD COLUMN id uuid NOT NULL DEFAULT gen_random_uuid();
ALTER TABLE invitations ADD CONSTRAINT invitations_id_unique UNIQUE(id);
ALTER TABLE invitations ADD COLUMN max_uses integer NOT NULL DEFAULT 1 CHECK(max_uses BETWEEN 1 AND 1000);
ALTER TABLE invitations ADD COLUMN uses integer NOT NULL DEFAULT 0;
UPDATE invitations SET uses=1 WHERE used_by IS NOT NULL;
ALTER TABLE invitations ADD CONSTRAINT invitations_uses_valid CHECK(uses BETWEEN 0 AND max_uses);
INSERT INTO schema_versions(version) VALUES (14) ON CONFLICT DO NOTHING;
