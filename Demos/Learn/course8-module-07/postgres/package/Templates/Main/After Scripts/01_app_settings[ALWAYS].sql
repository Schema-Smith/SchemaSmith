-- The integration settings the app reads at startup, written from the package's ScriptTokens
-- on every deploy ([ALWAYS]), so changing a token and redeploying is how a setting changes.
DELETE FROM app_setting WHERE setting_name IN ('region', 'webhook_url', 'deploy_key', 'report_api_key', 'audit_dsn');
INSERT INTO app_setting (setting_name, setting_value) VALUES ('region', '{{Region}}');
INSERT INTO app_setting (setting_name, setting_value) VALUES ('webhook_url', '{{WebhookUrl}}');
INSERT INTO app_setting (setting_name, setting_value) VALUES ('deploy_key', '{{DeployKey}}');
INSERT INTO app_setting (setting_name, setting_value) VALUES ('report_api_key', '{{ReportingApiKey}}');
INSERT INTO app_setting (setting_name, setting_value) VALUES ('audit_dsn', '{{AuditDsn}}');
