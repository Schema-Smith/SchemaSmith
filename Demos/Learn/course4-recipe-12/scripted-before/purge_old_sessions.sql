-- The "scripted" form: a guarded CREATE EVENT in an ordinary migration script.
-- Run it, edit the body, run it again -- and watch nothing happen.
CREATE EVENT IF NOT EXISTS purge_old_sessions
  ON SCHEDULE EVERY 1 DAY
  DO DELETE FROM app_session WHERE started_at < NOW() - INTERVAL 7 DAY;
