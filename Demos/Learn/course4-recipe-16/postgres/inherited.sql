-- The database you inherited: built by hand, never by a package.
CREATE TABLE public.device (
  device_id integer NOT NULL PRIMARY KEY,
  flags     bit(8) NOT NULL,
  mask      bit varying(16),
  label     text
);
INSERT INTO public.device VALUES (1, B'10110001', B'1010', 'sensor'), (2, B'00001111', NULL, 'relay');
