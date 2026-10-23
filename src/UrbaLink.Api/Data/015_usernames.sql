ALTER TABLE users ALTER COLUMN email DROP NOT NULL;
ALTER TABLE users ADD COLUMN username varchar(80);
-- Preserve existing display names and passwords. Disambiguate legacy duplicate names.
DO $$
DECLARE account record; candidate text; suffix integer;
BEGIN
    FOR account IN SELECT id,name FROM users ORDER BY created_at,id LOOP
        candidate := btrim(account.name);
        suffix := 0;
        WHILE EXISTS(SELECT 1 FROM users WHERE lower(username)=lower(candidate)) LOOP
            suffix := suffix+1;
            candidate := left(btrim(account.name),30)||'-'||replace(account.id::text,'-','')||'-'||suffix;
        END LOOP;
        UPDATE users SET username=candidate WHERE id=account.id;
    END LOOP;
END $$;
ALTER TABLE users ALTER COLUMN username SET NOT NULL;
CREATE UNIQUE INDEX users_username_unique ON users(lower(username));
INSERT INTO schema_versions(version) VALUES (15) ON CONFLICT DO NOTHING;
