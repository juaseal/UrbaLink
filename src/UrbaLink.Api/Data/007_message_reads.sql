CREATE TABLE market_message_reads (
    conversation_id bigint NOT NULL REFERENCES market_conversations(id),
    user_id uuid NOT NULL REFERENCES users(id),
    last_message_id bigint NOT NULL CHECK (last_message_id > 0),
    PRIMARY KEY(conversation_id,user_id)
);
INSERT INTO schema_versions(version) VALUES (7);
