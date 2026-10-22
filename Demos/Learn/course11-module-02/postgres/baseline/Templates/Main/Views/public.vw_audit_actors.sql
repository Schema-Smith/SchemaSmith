CREATE OR REPLACE VIEW public.vw_audit_actors AS
SELECT audit_id, actor FROM public.audit_trail;
