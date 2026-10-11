CREATE TABLE ride_details (
    post_id bigint PRIMARY KEY REFERENCES posts(id),
    community_id uuid NOT NULL REFERENCES communities(id),
    author_id uuid NOT NULL REFERENCES users(id),
    request_id uuid NOT NULL,
    origin varchar(80) NOT NULL,
    destination varchar(80) NOT NULL,
    days integer NOT NULL CHECK(days BETWEEN 1 AND 127),
    departure_minutes integer NOT NULL CHECK(departure_minutes BETWEEN 0 AND 1439),
    return_minutes integer CHECK(return_minutes BETWEEN 0 AND 1439),
    return_next_day boolean NOT NULL,
    seats integer NOT NULL CHECK(seats BETWEEN 1 AND 8),
    price_cents integer NOT NULL CHECK(price_cents BETWEEN 0 AND 1000000),
    allows_detour boolean NOT NULL,
    detour_details varchar(300) NOT NULL,
    meeting_point varchar(200) NOT NULL,
    notes varchar(1000) NOT NULL,
    UNIQUE(community_id,author_id,request_id),
    CHECK((return_minutes IS NULL AND NOT return_next_day) OR
        (return_minutes IS NOT NULL AND (return_next_day OR return_minutes>departure_minutes)))
);
INSERT INTO schema_versions(version) VALUES(8);
