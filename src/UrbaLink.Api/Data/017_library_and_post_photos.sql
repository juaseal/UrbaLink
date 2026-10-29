ALTER TABLE posts ADD COLUMN photo bytea CHECK(photo IS NULL OR octet_length(photo)<=40000);
CREATE TABLE books(
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 community_id uuid NOT NULL REFERENCES communities(id),owner_id uuid NOT NULL REFERENCES users(id),
 request_id uuid NOT NULL,request_hash text NOT NULL,
 title varchar(120) NOT NULL,author varchar(120) NOT NULL DEFAULT '',isbn varchar(32) NOT NULL DEFAULT '',
 condition text NOT NULL CHECK(condition IN ('New','LikeNew','Good','Worn')),
 audience text NOT NULL CHECK(audience IN ('Adult','Young','Child','Baby')),
 mode text NOT NULL CHECK(mode IN ('Lend','Pass')),description varchar(2000) NOT NULL DEFAULT '',
 photo bytea NOT NULL CHECK(octet_length(photo) BETWEEN 4 AND 40000),
 state text NOT NULL DEFAULT 'Available' CHECK(state IN ('Available','Lent','Passed','Withdrawn')),
 version integer NOT NULL DEFAULT 1,created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 UNIQUE(community_id,owner_id,request_id),
 CHECK((state<>'Lent' OR mode='Lend') AND (state<>'Passed' OR mode='Pass'))
);
CREATE INDEX books_community_catalog ON books(community_id,id DESC);
ALTER TABLE market_conversations ADD COLUMN book_id bigint REFERENCES books(id);
ALTER TABLE market_conversations DROP CONSTRAINT conversation_subject;
ALTER TABLE market_conversations ADD CONSTRAINT conversation_subject CHECK(num_nonnulls(item_id,post_id,animal_notice_id,book_id)=1);
ALTER TABLE market_conversations ADD CONSTRAINT book_conversation_unique UNIQUE(book_id,interested_user_id);
INSERT INTO schema_versions(version) VALUES(17);
