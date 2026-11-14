# Settings a first run needs

Each tool reads `<Tool>.settings.json` in the current folder, or the file named with `--ConfigFile:<path>`, then `SmithySettings_` environment variables (`:` written as `__`), then `--Key:value` on the command line. Later sources win. Settings files hold no passwords: see `passwords.md`.

## SchemaTongs (extraction)

```json
{
  "Source": {
    "Platform": "SqlServer",
    "Server": "localhost",
    "Port": "",
    "Database": "Shop",
    "User": ""
  },
  "Product": {
    "Name": "Shop",
    "Path": "./shop-package"
  }
}
```

`Source:Platform` is `SqlServer`, `PostgreSQL`, `MySQL` or `MariaDb`. A blank `Port` uses the engine's default. On SQL Server a blank `User` means Windows authentication. `Product:Path` is the folder the package is written to.

## SchemaQuench (validate, preview, deploy, drift)

```json
{
  "SchemaPackagePath": "./shop-package",
  "Target": {
    "Server": "localhost",
    "Port": "",
    "User": "",
    "TemplateTargets": {
      "Shop": { "Databases": [ "Shop_test" ] }
    }
  }
}
```

`SchemaPackagePath` is the folder holding the package's `Product.json`. A template deploys to the databases its `DatabaseIdentificationScript` names. A package extracted by SchemaTongs has none, so name the database for each template under `Target:TemplateTargets:<template>:Databases`; the template's name is in the package's `Product.json` (`TemplateOrder`). Add `"CreateIfMissing": true` beside `Databases` to create a database that does not exist yet.

## Log folder

Pass `--LogPath:<folder>` on every run so the logs and the deployment summary land somewhere known (see `outputs.md`).
