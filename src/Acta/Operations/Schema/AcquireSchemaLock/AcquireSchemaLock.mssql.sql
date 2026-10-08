DECLARE @rc INT;
EXEC @rc = sp_getapplock @p_key, 'Exclusive', 'Transaction', 60000;
IF @rc < 0
    THROW 50000, 'sp_getapplock failed acquiring acta-migrations lock', 1;
-- The grant code, so every dialect answers one value.
SELECT @rc;
