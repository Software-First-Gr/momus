-- Marking an insight fixed and having it come back is a different story from it never having
-- gone away, and the status column alone cannot tell them apart: an insight that fires again is
-- reopened immediately, so by the time anyone looks it is simply "open" again.

ALTER TABLE insights ADD COLUMN reopened INTEGER NOT NULL DEFAULT 0;
