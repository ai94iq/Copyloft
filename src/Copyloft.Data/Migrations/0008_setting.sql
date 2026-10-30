-- 0008: lock state (the password hash, unlocked categories, wrong-password count) moves out of
-- settings.json into the encrypted database, so it cannot be read or edited without the key.
CREATE TABLE Setting (
    Key TEXT NOT NULL PRIMARY KEY,
    Value TEXT NOT NULL
);
