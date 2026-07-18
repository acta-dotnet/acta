CREATE OR ALTER PROCEDURE {{schema}}.release_lock
    @p_lease_key VARCHAR(256),
    @p_version   INT
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM {{schema}}.leases
     OUTPUT deleted.version
     WHERE lease_key = @p_lease_key AND version = @p_version;
END;
GO
