-- جداول إدارة أخطاء وصلاحيات المزامنة.
-- تنفذ داخل كل قاعدة بيانات محلية تريد تشغيل برنامج المزامنة عليها.

CREATE TABLE IF NOT EXISTS tbl_sync_errors
(
    error_id BIGINT NOT NULL AUTO_INCREMENT,
    direction VARCHAR(10) NOT NULL,
    related_id BIGINT NOT NULL,
    event_uuid VARCHAR(36) NULL,
    entity_type VARCHAR(30) NOT NULL,
    entity_uuid VARCHAR(36) NOT NULL,
    local_id BIGINT NULL,
    operation_type INT NOT NULL,
    error_code VARCHAR(60) NOT NULL,
    error_message TEXT NOT NULL,
    error_details LONGTEXT NULL,
    resolution_status INT NOT NULL DEFAULT 0,
    requires_support TINYINT NOT NULL DEFAULT 0,
    created_at DATETIME NOT NULL,
    updated_at DATETIME NOT NULL,
    resolved_at DATETIME NULL,
    PRIMARY KEY (error_id),
    KEY ix_sync_errors_open (resolution_status, created_at),
    KEY ix_sync_errors_entity (entity_uuid, resolution_status),
    KEY ix_sync_errors_related (direction, related_id)
) ENGINE=MyISAM DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS tbl_sync_permissions
(
    permission_key VARCHAR(60) NOT NULL,
    permission_enabled TINYINT NOT NULL DEFAULT 0,
    password_hash VARCHAR(128) NULL,
    password_salt VARCHAR(64) NULL,
    updated_at DATETIME NOT NULL,
    PRIMARY KEY (permission_key)
) ENGINE=MyISAM DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

INSERT IGNORE INTO tbl_sync_permissions
    (permission_key, permission_enabled, updated_at)
VALUES
    ('CancelSyncEvent', 0, NOW()),
    ('MergeInvoiceIdentity', 0, NOW());

-- إعداد كلمة مرور الدعم يتم مرة واحدة بواسطة الدعم الفني فقط.
-- استبدل CHANGE_ME بكلمة المرور المطلوبة ثم احذف الأمر من سجل الاستعلامات إن أمكن.
-- SET @support_salt = REPLACE(UUID(), '-', '');
-- UPDATE tbl_sync_permissions
-- SET permission_enabled = 1,
--     password_salt = @support_salt,
--     password_hash = SHA2(CONCAT(@support_salt, 'CHANGE_ME'), 256),
--     updated_at = NOW()
-- WHERE permission_key = 'CancelSyncEvent';
