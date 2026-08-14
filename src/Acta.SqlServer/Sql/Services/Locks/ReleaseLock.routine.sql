CREATE OR ALTER PROCEDURE {{schema}}.release_lock
    @p_lock_key VARCHAR(256),
    @p_hold_token UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM {{schema}}.locks
    OUTPUT DELETED.hold_token
    WHERE lock_key = @p_lock_key AND hold_token = @p_hold_token;
END;
GO
