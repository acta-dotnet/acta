DECLARE @rc int;
EXEC @rc = sp_getapplock @p_key, 'Exclusive', 'Transaction', 60000;
IF @rc < 0
    THROW 50000, 'sp_getapplock failed acquiring acta-migrations lock', 1;
