CREATE TABLE clothing_donations(
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 community_id uuid NOT NULL REFERENCES communities(id),owner_id uuid NOT NULL REFERENCES users(id),
 request_id uuid NOT NULL,request_hash text NOT NULL,
 title varchar(120) NOT NULL CHECK(length(trim(title))>0),
 season text NOT NULL CHECK(season IN ('AllYear','Winter','Summer','MidSeason')),
 size varchar(40) NOT NULL CHECK(length(trim(size))>0),
 condition text NOT NULL CHECK(condition IN ('New','LikeNew','Good','Worn')),
 audience text NOT NULL CHECK(audience IN ('Adult','Young','Child','Baby')),
 quantity integer NOT NULL CHECK(quantity BETWEEN 1 AND 1000),
 description varchar(2000) NOT NULL DEFAULT '',
 photo bytea CHECK(photo IS NULL OR octet_length(photo) BETWEEN 4 AND 40000),
 state text NOT NULL DEFAULT 'Available' CHECK(state IN ('Available','Reserved','Given','Withdrawn')),
 version integer NOT NULL DEFAULT 1,created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 UNIQUE(community_id,owner_id,request_id)
);
CREATE INDEX donations_catalog ON clothing_donations(community_id,state,id DESC);
CREATE INDEX donations_owner ON clothing_donations(community_id,owner_id,id DESC);
ALTER TABLE market_conversations ADD COLUMN donation_id bigint REFERENCES clothing_donations(id);
ALTER TABLE market_conversations DROP CONSTRAINT conversation_subject;
ALTER TABLE market_conversations ADD CONSTRAINT conversation_subject CHECK(num_nonnulls(item_id,post_id,animal_notice_id,book_id,donation_id)=1);
ALTER TABLE market_conversations ADD CONSTRAINT donation_conversation_unique UNIQUE(donation_id,interested_user_id);

-- A single subject projection for inboxes, unread notices and message reports.
-- Community membership and participant authorization are applied by each caller.
CREATE VIEW conversation_subjects AS
 SELECT t.id,t.interested_user_id,i.id AS subject_id,i.community_id,i.owner_id,'Object'::text AS kind,i.title
 FROM market_conversations t JOIN market_items i ON i.id=t.item_id
 UNION ALL
 SELECT t.id,t.interested_user_id,p.id,p.community_id,p.author_id,'Service',p.title
 FROM market_conversations t JOIN posts p ON p.id=t.post_id AND p.category='Ofrezco' AND p.deleted_at IS NULL
 UNION ALL
 SELECT t.id,t.interested_user_id,n.id,n.community_id,n.author_id,'Animal',
   (CASE n.species WHEN 'Dog' THEN 'Perro' ELSE 'Gato' END)||(CASE n.kind WHEN 'Lost' THEN ' perdido' ELSE ' encontrado' END)
 FROM market_conversations t JOIN animal_notices n ON n.id=t.animal_notice_id
 UNION ALL
 SELECT t.id,t.interested_user_id,b.id,b.community_id,b.owner_id,'Book',b.title
 FROM market_conversations t JOIN books b ON b.id=t.book_id
 UNION ALL
 SELECT t.id,t.interested_user_id,d.id,d.community_id,d.owner_id,'Donation',d.title
 FROM market_conversations t JOIN clothing_donations d ON d.id=t.donation_id;
INSERT INTO schema_versions(version) VALUES(19);
