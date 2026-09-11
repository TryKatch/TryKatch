\getenv database_name POSTGRES_DB
\getenv migrator_password TRYKATCH_MIGRATOR_PASSWORD
\getenv org_runtime_password TRYKATCH_ORG_RUNTIME_PASSWORD
\getenv platform_runtime_password TRYKATCH_PLATFORM_RUNTIME_PASSWORD
\getenv identity_runtime_password TRYKATCH_IDENTITY_RUNTIME_PASSWORD
\getenv outbox_worker_password TRYKATCH_OUTBOX_WORKER_PASSWORD

SELECT format(
  'CREATE ROLE %I WITH LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS',
  'trykatch_migrator', :'migrator_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'trykatch_migrator') \gexec

SELECT format(
  'ALTER ROLE %I WITH LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS',
  'trykatch_migrator', :'migrator_password') \gexec

SELECT format('CREATE ROLE %I WITH LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', 'trykatch_org_runtime', :'org_runtime_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'trykatch_org_runtime') \gexec
SELECT format('ALTER ROLE %I WITH LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', 'trykatch_org_runtime', :'org_runtime_password') \gexec

SELECT format('CREATE ROLE %I WITH LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', 'trykatch_platform_runtime', :'platform_runtime_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'trykatch_platform_runtime') \gexec
SELECT format('ALTER ROLE %I WITH LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', 'trykatch_platform_runtime', :'platform_runtime_password') \gexec

SELECT format('CREATE ROLE %I WITH LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', 'trykatch_identity_runtime', :'identity_runtime_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'trykatch_identity_runtime') \gexec
SELECT format('ALTER ROLE %I WITH LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', 'trykatch_identity_runtime', :'identity_runtime_password') \gexec

SELECT format('CREATE ROLE %I WITH LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', 'trykatch_outbox_worker', :'outbox_worker_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'trykatch_outbox_worker') \gexec
SELECT format('ALTER ROLE %I WITH LOGIN PASSWORD %L NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', 'trykatch_outbox_worker', :'outbox_worker_password') \gexec

SELECT format('ALTER DATABASE %I OWNER TO %I', :'database_name', 'trykatch_migrator') \gexec
