# 資料庫評估書：多裝置同步之資料庫整併、關聯維護與 Keep Both 派生規格 (DBA-ISSUE-0052-v0.1)

- **對應 Issue ID**：`ISSUE-0052`
- **評估版本**：`v0.1`
- **評估日期**：`2026-10-18`
- **DBA 角色**：DBA Agent

---

## 1. 資料庫結構與關係盤點

整併作業涉及之核心資料表：
1. `contact_records`：名片主表（PK: `contact_id` UUID，含 `display_name_primary`、`company_primary`、`job_title_primary`、`notes`、`created_at_utc`、`updated_at_utc`、`pending_delete`）。
2. `contact_values`：聯絡資訊與自訂欄位表（PK: `value_id` UUID，FK: `contact_id`，含 `field_key`、`value_text`、`normalized_value`、`sort_order`、`is_primary`）。
3. `card_image_assets`：名片影像表（PK: `asset_id` UUID，FK: `contact_id`，含 `side`、`relative_path`、`sha256`、`width`、`height`）。
4. `contact_tag_catalog`：全域標籤目錄（PK: `tag_id` UUID，含 `tag_name`、`normalized_name`）。
5. `contact_record_tags`：名片與標籤關聯表（複合 PK 或獨立記錄，含 `contact_id`、`tag_id`、`assigned_at_utc`）。

---

## 2. 衝突裁決之資料庫實作規則

### 2.1 外部新增 (`OnlyInRemote`)
1. **標籤目錄比對**：
   - 查詢外部名片關聯的標籤，檢查本機 `contact_tag_catalog` 是否已有同名（`normalized_name`）標籤。
   - 若本機已有同名標籤，複用本機的 `tag_id`；若無，則將外部標籤記錄寫入本機 `contact_tag_catalog`。
2. **主表記錄寫入**：
   - 將外部 `contact_records` 整筆 INSERT 至本機。
3. **EAV 聯絡值寫入**：
   - 將外部 `contact_values` 整批 INSERT 至本機。
4. **影像資產寫入與圖檔拷貝**：
   - 複製實體檔案至本機 `card-images/` 目錄。
   - 將外部 `card_image_assets` 記錄 INSERT 至本機。
5. **標籤關聯寫入**：
   - 將對應的 `(contact_id, resolved_tag_id)` 寫入 `contact_record_tags`。

### 2.2 採納外部 (`KeepRemote` / 較新外部)
1. **清除本機既有關聯**：
   - 在 Transaction 內執行：
     ```sql
     DELETE FROM contact_values WHERE contact_id = @id;
     DELETE FROM contact_record_tags WHERE contact_id = @id;
     DELETE FROM card_image_assets WHERE contact_id = @id;
     ```
2. **更新主表記錄**：
   - UPDATE `contact_records` 為外部對應欄位值與 `updated_at_utc`。
3. **重新寫入外部關聯**：
   - 比照 2.1 流程，重新寫入外部的 `contact_values`、`contact_record_tags` 及 `card_image_assets`，並覆寫實體圖檔。

### 2.3 保留本機 (`KeepLocal` / 較新本機)
- 本機資料庫完全不予變動，略過該筆名片。

### 2.4 兩側同時保留 (`KeepBoth`)
1. **本機記錄原樣保留**：
   - 原始 `contact_id` 的本機紀錄與所有關聯檔案保持不動。
2. **外部記錄派生新 UUID**：
   - 產生全新 `new_contact_id = Guid.NewGuid().ToString("D")`。
3. **主表記錄派生寫入**：
   - 讀取外部 `contact_records`，將其 `contact_id` 替換為 `new_contact_id`。
   - 名片姓名或備註附加註記（例如：`{display_name_primary} (同步複本)`）。
   - INSERT 至本機 `contact_records`。
4. **聯絡值派生寫入**：
   - 讀取外部 `contact_values`，生成新的 `value_id`，將 `contact_id` 指向 `new_contact_id`，整批 INSERT。
5. **標籤關聯派生寫入**：
   - 讀取外部標籤關聯，將 `contact_id` 指向 `new_contact_id`，寫入本機 `contact_record_tags`。
6. **影像資產派生重命名**：
   - 檢查外部名片是否有正反面圖檔。
   - 若有圖檔，產生新的實體檔名：`card-images/{new_contact_id}_front.jpg` 及 `card-images/{new_contact_id}_back.jpg`。
   - 將外部圖檔複製至本機並存為新檔名。
   - 產生新的 `asset_id`，以新路徑寫入本機 `card_image_assets`。

---

## 3. 事務保證 (Transaction & Rollback) 與安全快照

```csharp
using var tx = connection.BeginTransaction();
try
{
    // 依序執行整併 SQL
    ...
    tx.Commit();
}
catch
{
    tx.Rollback();
    // 拋出異常並還原圖檔變更
    throw;
}
```

- **安全快照防護**：在開啟寫入交易前，先將本機 `contacts.sqlite3` 完整拷貝至 `backups/pre_merge_YYYYMMDD_HHmmss.sqlite3`，確保操作 100% 可逆。
