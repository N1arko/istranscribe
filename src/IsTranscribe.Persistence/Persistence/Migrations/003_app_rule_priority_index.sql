ALTER TABLE app_rule ADD COLUMN priority_index INTEGER NOT NULL DEFAULT 0;
CREATE INDEX IF NOT EXISTS idx_app_rule_priority_index ON app_rule(priority_index);
