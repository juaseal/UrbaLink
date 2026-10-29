ALTER TABLE books ALTER COLUMN photo DROP NOT NULL;
INSERT INTO schema_versions(version) VALUES(18);
