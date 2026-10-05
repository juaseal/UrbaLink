CREATE TABLE posts (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    community_id uuid NOT NULL REFERENCES communities(id) ON DELETE CASCADE,
    author_id uuid NOT NULL REFERENCES users(id),
    title varchar(120) NOT NULL CHECK (length(trim(title)) BETWEEN 3 AND 120),
    body text NOT NULL CHECK (length(trim(body)) BETWEEN 1 AND 4000),
    category varchar(20) NOT NULL CHECK (category IN ('General','Ayuda','Ofrezco','Material','Coche','Aviso oficial')),
    created_at timestamptz NOT NULL DEFAULT now(),
    deleted_at timestamptz,
    deleted_by uuid REFERENCES users(id)
);
CREATE INDEX posts_community_feed ON posts(community_id, id DESC) WHERE deleted_at IS NULL;
CREATE INDEX posts_community_category_feed ON posts(community_id, category, id DESC) WHERE deleted_at IS NULL;
INSERT INTO schema_versions(version) VALUES (2);
