-- Run once by a DBA after migrations. Replace [CORP\svc-icc-portal] with the portal's database login/user.
-- The portal must be able to read and add audit rows, and nothing else on this table.
DENY UPDATE, DELETE ON dbo.AuditLog TO [CORP\svc-icc-portal];
GRANT SELECT, INSERT ON dbo.AuditLog TO [CORP\svc-icc-portal];
-- The portal login must NOT be db_owner / sysadmin in production.
-- Use a separate migration account to run 'dotnet ef database update'.
