CREATE TABLE animal_notices (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 community_id uuid NOT NULL REFERENCES communities(id),
 author_id uuid NOT NULL REFERENCES users(id),
 request_id uuid NOT NULL,
 request_hash text NOT NULL,
 kind text NOT NULL CHECK(kind IN ('Lost','Found')),
 species text NOT NULL CHECK(species IN ('Cat','Dog')),
 name varchar(80) NOT NULL,
 location varchar(160) NOT NULL,
 occurred_at timestamptz NOT NULL,
 description varchar(2000) NOT NULL,
 is_with_reporter boolean NOT NULL,
 photo bytea,
 status text NOT NULL DEFAULT 'Active' CHECK(status IN ('Active','Recovered','Withdrawn')),
 created_at timestamptz NOT NULL DEFAULT now(),
 updated_at timestamptz NOT NULL DEFAULT now(),
 closed_at timestamptz,
 version integer NOT NULL DEFAULT 1,
 UNIQUE(community_id,author_id,request_id),
 CHECK (photo IS NULL OR octet_length(photo)<=40000),
 CHECK ((status='Active')=(closed_at IS NULL))
);
CREATE INDEX animal_notice_feed ON animal_notices(community_id,status,id DESC);
CREATE TABLE animal_notice_history (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 notice_id bigint NOT NULL REFERENCES animal_notices(id),
 actor_id uuid NOT NULL REFERENCES users(id),
 action text NOT NULL,
 snapshot jsonb NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE market_conversations ADD COLUMN animal_notice_id bigint REFERENCES animal_notices(id);
ALTER TABLE market_conversations DROP CONSTRAINT conversation_subject;
ALTER TABLE market_conversations ADD CONSTRAINT conversation_subject CHECK(num_nonnulls(item_id,post_id,animal_notice_id)=1);
ALTER TABLE market_conversations ADD CONSTRAINT animal_conversation_unique UNIQUE(animal_notice_id,interested_user_id);
INSERT INTO schema_versions(version) VALUES(12);

