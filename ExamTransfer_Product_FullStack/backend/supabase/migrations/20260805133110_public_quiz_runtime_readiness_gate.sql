begin;

update public.examtransfer_cloud_meta
set schema_version = 29,
    updated_at = pg_catalog.now()
where id = 1
  and schema_version < 29;

commit;
