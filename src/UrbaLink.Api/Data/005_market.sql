CREATE TABLE market_items (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    community_id uuid NOT NULL REFERENCES communities(id),
    owner_id uuid NOT NULL REFERENCES users(id),
    request_id uuid NOT NULL,
    title varchar(120) NOT NULL,
    description varchar(4000) NOT NULL,
    mode varchar(10) NOT NULL CHECK (mode IN ('Sale','Rent','Auction')),
    price_cents integer NOT NULL CHECK (price_cents BETWEEN 0 AND 100000000),
    period varchar(10),
    ends_at timestamptz,
    bid_step_cents integer NOT NULL DEFAULT 0,
    photo bytea,
    state varchar(15) NOT NULL DEFAULT 'Active' CHECK (state IN ('Active','Withdrawn','Completed')),
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(community_id,owner_id,request_id),
    CHECK ((mode='Rent' AND period IN ('Hour','Day','Week')) OR (mode<>'Rent' AND period IS NULL)),
    CHECK ((mode='Auction' AND ends_at IS NOT NULL AND bid_step_cents BETWEEN 1 AND 100000000) OR
        (mode<>'Auction' AND ends_at IS NULL AND bid_step_cents=0)),
    CHECK (photo IS NULL OR octet_length(photo)<=40000)
);
CREATE INDEX market_feed ON market_items(community_id,id DESC);
CREATE TABLE market_bids (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    item_id bigint NOT NULL REFERENCES market_items(id),
    user_id uuid NOT NULL REFERENCES users(id),
    request_id uuid NOT NULL,
    amount_cents integer NOT NULL CHECK (amount_cents BETWEEN 1 AND 100000000),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE(item_id,user_id,request_id),
    UNIQUE(item_id,amount_cents)
);
CREATE INDEX market_bid_highest ON market_bids(item_id,amount_cents DESC);
CREATE TABLE market_requests (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    item_id bigint NOT NULL REFERENCES market_items(id),
    user_id uuid NOT NULL REFERENCES users(id),
    request_id uuid NOT NULL,
    starts_at timestamptz,
    ends_at timestamptz,
    total_cents bigint NOT NULL CHECK (total_cents>=0),
    note varchar(1000) NOT NULL,
    state varchar(15) NOT NULL DEFAULT 'Pending' CHECK (state IN ('Pending','Accepted','Rejected','Cancelled','Completed')),
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(item_id,user_id,request_id),
    CHECK ((starts_at IS NULL AND ends_at IS NULL) OR (starts_at IS NOT NULL AND ends_at>starts_at))
);
CREATE INDEX market_request_item ON market_requests(item_id,state);
INSERT INTO schema_versions(version) VALUES (5);
