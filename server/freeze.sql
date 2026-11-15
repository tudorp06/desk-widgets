-- Closes the Supabase gallery to anything new. Run once, by hand, in the Supabase SQL editor,
-- after schema.sql. It is safe to run again.
--
-- Why: the gallery now lives in the repository (widgets/) and is published as files on the
-- website. Copies of the app older than 1.9 still read this database, and they must keep
-- working until people have updated, so nothing is deleted here. What stops is everything that
-- would add to it, because a widget accepted here would exist in this gallery and not in the
-- real one:
--
--   - uploading a widget or a new version
--   - approving a version that is still waiting (bring it over with a pull request instead)
--   - asking for an account
--
-- What keeps working, for those older copies: reading Explore, installing an approved version,
-- signing in, likes and install counts.
--
-- To undo it, run schema.sql again: it restores every grant below.

revoke insert on public.widgets, public.versions from anon, authenticated;

revoke execute on function public.approve(text, int, text) from public, anon, authenticated;
revoke execute on function public.request_access(text) from public, anon, authenticated;
