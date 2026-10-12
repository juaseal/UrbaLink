ALTER TABLE posts DROP CONSTRAINT posts_category_check;
ALTER TABLE posts ADD CONSTRAINT posts_category_check CHECK (category IN ('General','Ayuda','Ofrezco','Recomendación','Material','Coche','Aviso oficial'));
INSERT INTO schema_versions(version) VALUES (10);
