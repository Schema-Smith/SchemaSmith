CREATE OR REPLACE TRIGGER price_history_stamp
  BEFORE INSERT ON public.price_history
  FOR EACH ROW EXECUTE FUNCTION public.stamp_recorded_at();
