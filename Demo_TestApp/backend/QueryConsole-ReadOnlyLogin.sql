-- Creates the read-only login the Query Console connects as (ConnectionStrings:QueryConsole).
-- Run once as a sysadmin. Change the password first, then put the same value in the connection string, e.g.
--   Server=localhost,1433;Database=HealthAppDb;User Id=querycon_ro;Password=<password>;TrustServerCertificate=True
--
-- The login can read the HealthApp database and nothing else: no write rights, no other databases, no server
-- permissions. The Query Console's keyword screen is only a guard rail; this login is the actual boundary.

USE [master];
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'querycon_ro')
    CREATE LOGIN [querycon_ro] WITH PASSWORD = N'CHANGE_ME_Str0ng!Passw0rd', CHECK_POLICY = ON;
GO

USE [HealthAppDb];
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'querycon_ro')
    CREATE USER [querycon_ro] FOR LOGIN [querycon_ro];
ALTER ROLE [db_datareader] ADD MEMBER [querycon_ro];
-- Belt and braces: db_datareader is read-only, but make the intent explicit.
DENY INSERT, UPDATE, DELETE, EXECUTE, ALTER, CONTROL TO [querycon_ro];
GO
