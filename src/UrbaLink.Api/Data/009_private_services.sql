ALTER TABLE market_conversations ALTER COLUMN item_id DROP NOT NULL;
ALTER TABLE market_conversations ADD COLUMN post_id bigint REFERENCES posts(id);
ALTER TABLE market_conversations ADD CONSTRAINT conversation_subject CHECK (num_nonnulls(item_id,post_id)=1);
ALTER TABLE market_conversations ADD CONSTRAINT service_conversation_unique UNIQUE(post_id,interested_user_id);
INSERT INTO schema_versions(version) VALUES (9);
