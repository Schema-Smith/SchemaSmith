-- Fills recorded_at when an insert leaves it out.
CREATE OR REPLACE FUNCTION public.stamp_recorded_at() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.recorded_at IS NULL THEN
    NEW.recorded_at := now();
  END IF;
  RETURN NEW;
END
$$;
