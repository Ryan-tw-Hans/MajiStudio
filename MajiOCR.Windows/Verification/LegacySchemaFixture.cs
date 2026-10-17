namespace MajiOCR_Windows.Verification;
internal static class LegacySchemaFixture {
internal const string Schema = """
CREATE TABLE IF NOT EXISTS contact_records (
                        contact_id TEXT PRIMARY KEY,
                        contract_version TEXT NOT NULL,
                        revision INTEGER NOT NULL CHECK (revision >= 1),
                        persistence_state TEXT NOT NULL CHECK (persistence_state IN ('PendingFinalize', 'Ready', 'Failed', 'ImageMissing')),
                        completion_source TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL,
                        completed_at TEXT,
                        save_operation_id TEXT NOT NULL,
                        content_fingerprint TEXT NOT NULL,
                        pending_delete INTEGER NOT NULL DEFAULT 0 CHECK (pending_delete IN (0, 1))
                    );

                    CREATE INDEX IF NOT EXISTS idx_contact_records_state_updated ON contact_records (persistence_state, updated_at DESC);
                    CREATE INDEX IF NOT EXISTS idx_contact_records_pending_delete ON contact_records (pending_delete);
                    CREATE INDEX IF NOT EXISTS idx_contact_records_save_op ON contact_records (save_operation_id);

                    CREATE TABLE IF NOT EXISTS contact_values (
                        value_id TEXT PRIMARY KEY,
                        contact_id TEXT NOT NULL REFERENCES contact_records(contact_id) ON DELETE CASCADE,
                        value_kind TEXT NOT NULL,
                        custom_field_definition_id TEXT,
                        custom_field_name_snapshot TEXT,
                        raw_value TEXT NOT NULL,
                        normalized_value TEXT,
                        label TEXT NOT NULL,
                        ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
                        source_mode TEXT NOT NULL,
                        locked_at TEXT NOT NULL,
                        UNIQUE (contact_id, ordinal)
                    );

                    CREATE INDEX IF NOT EXISTS idx_contact_values_contact ON contact_values (contact_id);
                    CREATE INDEX IF NOT EXISTS idx_contact_values_kind ON contact_values (value_kind);
                    CREATE INDEX IF NOT EXISTS idx_contact_values_search ON contact_values (value_kind, raw_value);

                    CREATE TABLE IF NOT EXISTS card_image_assets (
                        asset_id TEXT PRIMARY KEY,
                        contact_id TEXT NOT NULL REFERENCES contact_records(contact_id) ON DELETE CASCADE,
                        capture_source TEXT NOT NULL,
                        storage_state TEXT NOT NULL CHECK (storage_state IN ('PendingFinalize', 'Ready', 'Failed', 'ImageMissing', 'PendingDelete')),
                        relative_path TEXT NOT NULL UNIQUE,
                        staging_relative_path TEXT,
                        sha256 TEXT NOT NULL,
                        mime_type TEXT NOT NULL,
                        byte_count INTEGER NOT NULL CHECK (byte_count > 0),
                        width INTEGER NOT NULL CHECK (width > 0),
                        height INTEGER NOT NULL CHECK (height > 0),
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS idx_card_image_assets_contact ON card_image_assets (contact_id);

                    CREATE TABLE IF NOT EXISTS pending_delete (
                        job_id TEXT PRIMARY KEY,
                        contact_id TEXT NOT NULL REFERENCES contact_records(contact_id) ON DELETE CASCADE UNIQUE,
                        asset_relative_path TEXT,
                        created_at TEXT NOT NULL,
                        status TEXT NOT NULL CHECK (status IN ('Pending', 'Processing', 'Failed')),
                        retry_count INTEGER NOT NULL DEFAULT 0,
                        last_error TEXT
                    );

                    CREATE INDEX IF NOT EXISTS idx_pending_delete_status ON pending_delete (status);

                    PRAGMA user_version = 1;
""";
}
