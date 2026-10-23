IF NOT EXISTS (SELECT 1 FROM master.sys.databases WHERE [name] = '{{TenantCRMDb}}')
BEGIN
    CREATE DATABASE [{{TenantCRMDb}}];
END
