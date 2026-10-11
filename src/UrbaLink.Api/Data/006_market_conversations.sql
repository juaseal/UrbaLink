-- Additive migration: any data from the early reservation prototype is preserved,
-- but those records are no longer used by the application.
ALTER TABLE market_items ADD COLUMN is_lent boolean NOT NULL DEFAULT false;
CREATE TABLE market_conversations (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    item_id bigint NOT NULL REFERENCES market_items(id),
    interested_user_id uuid NOT NULL REFERENCES users(id),
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(item_id,interested_user_id)
);
CREATE TABLE market_messages (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    conversation_id bigint NOT NULL REFERENCES market_conversations(id),
    author_id uuid NOT NULL REFERENCES users(id),
    request_id uuid NOT NULL,
    body varchar(2000) NOT NULL CHECK (length(trim(body)) BETWEEN 1 AND 2000),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE(conversation_id,author_id,request_id)
);
CREATE INDEX market_messages_history ON market_messages(conversation_id,id DESC);
INSERT INTO schema_versions(version) VALUES (6);
