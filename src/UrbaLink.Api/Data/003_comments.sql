CREATE TABLE comments (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    post_id bigint NOT NULL REFERENCES posts(id) ON DELETE CASCADE,
    author_id uuid NOT NULL REFERENCES users(id),
    body text NOT NULL CHECK (length(trim(body)) BETWEEN 1 AND 2000),
    request_id uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    deleted_at timestamptz,
    deleted_by uuid REFERENCES users(id),
    UNIQUE(post_id, author_id, request_id)
);
CREATE INDEX comments_post_feed ON comments(post_id, id) WHERE deleted_at IS NULL;
INSERT INTO schema_versions(version) VALUES (3);
