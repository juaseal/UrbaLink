CREATE INDEX memberships_community_user ON memberships(community_id,user_id);
CREATE INDEX invitations_community ON invitations(community_id);
CREATE INDEX sessions_expiry ON sessions(expires_at);
CREATE INDEX conversations_interested ON market_conversations(interested_user_id);
CREATE INDEX service_posts_author ON posts(author_id,id) WHERE category='Ofrezco' AND deleted_at IS NULL;
CREATE INDEX market_items_owner ON market_items(owner_id,id);
INSERT INTO schema_versions(version) VALUES (11);
