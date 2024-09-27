DO $$
DECLARE
	appRoleName TEXT := 'SnoopDawg';
BEGIN
	IF NOT EXISTS (SELECT * FROM pg_catalog.pg_roles WHERE rolname = 'SnoopDawg') THEN
		CREATE ROLE SnoopDawg WITH
			LOGIN
			NOSUPERUSER
			INHERIT
			ENCRYPTED PASSWORD 'SnoopDawg'; -- CHANGE THIS ON PRODUCTION
	END IF;
END
$$ -- migration tool: Migrondi