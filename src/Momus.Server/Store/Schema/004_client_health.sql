-- The client's report on itself, kept per window.
--
-- A client that is dropping operations or running an old build is invisible from the server side
-- otherwise: the evidence is in the application's log, and the person looking at Momus is not
-- tailing the application. Both columns are nullable because a client that predates them is a
-- perfectly good client — the contract only ever grows.

ALTER TABLE windows ADD COLUMN client_version TEXT;
ALTER TABLE windows ADD COLUMN dropped INTEGER NOT NULL DEFAULT 0;
