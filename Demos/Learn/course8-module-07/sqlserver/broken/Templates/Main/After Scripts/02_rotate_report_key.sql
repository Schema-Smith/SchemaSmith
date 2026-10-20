-- Rotate the reporting API key and record the rotation. key_rotation_log was never added to
-- the package, so this script fails -- the failure whose artifact Step 6 reads.
DELETE FROM dbo.app_setting WHERE setting_name = 'report_api_key';
INSERT INTO dbo.app_setting (setting_name, setting_value) VALUES ('report_api_key', '{{ReportingApiKey}}');
INSERT INTO key_rotation_log (setting_name, rotated_at) VALUES ('report_api_key', CURRENT_TIMESTAMP);
