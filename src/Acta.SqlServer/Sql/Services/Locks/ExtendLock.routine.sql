CREATE OR ALTER PROCEDURE {{schema}}.extend_lock
    @p_lease_key         VARCHAR(256),
    @p_version           INT,
    @p_lease_ttl_seconds INT
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE {{schema}}.leases
       SET expires_at_utc = DATEADD(SECOND, @p_lease_ttl_seconds, SYSUTCDATETIME())
     OUTPUT inserted.version
     WHERE lease_key = @p_lease_key AND version = @p_version;
END;
GO
