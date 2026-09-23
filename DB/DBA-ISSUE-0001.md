# 資料庫影響評估：ISSUE-0001 本機聯絡人 SQLite

| 項目 | 內容 |
| --- | --- |
| 文件版本 | 0.1.0-draft |
| 文件狀態 | 有條件採用；未授權建立 App 資料庫 |
| 對應 Issue | ISSUE-0001 |
| 日期 | 2026-09-23，UTC+08:00 |
| DBA 執行者／審查方式 | Codex；同一 Agent 的 DBA 自查，非獨立審查 |
| 關聯需求 | Requirements/RS-ISSUE-0001-v0.2.md |
| 關聯架構 | Architecture/AR-ISSUE-0001-v0.2.md |

## 1. 需求與結論

使用者已確認：完成的名片聯絡人資訊必須存入手機本機 SQLite，供後續查詢系統使用。

建議使用 Android Room 作為 SQLite 存取層。Room 以 SQLite 為底層，但提供 DAO、交易、編譯期 SQL 檢查及受控 migration，較適合本系統的結構化聯絡人資料。

DBA 結論為「有條件採用」：可儲存完成的結構化聯絡人資料，但原始名片影像、即時影格、完整 OCR 原文和長期供應商 API key 預設不得存入資料庫。資料庫加密與 Android Auto Backup 策略仍需人類決定。

## 2. 資料邊界與生命週期

| 資料 | 儲存位置 | 保存時點 | 預設保存策略 |
| --- | --- | --- | --- |
| Live OCR 候選、影格、位置框 | 記憶體／當次暫存 | 掃描期間 | 結束或放棄後清除。 |
| Locked 草稿 | 記憶體／當次暫存 | 使用者鎖定後 | 按完成前可修改或放棄；不作永久資料。 |
| 完成聯絡人與子欄位 | App 私有 Room / SQLite | 完成交易成功後 | 保留供本機查詢。 |
| Android Contacts 匯出狀態 | App 私有 Room / SQLite | 匯出完成、取消或失敗後 | 保留狀態，不取代本機記錄。 |
| 名片影像、完整 OCR 原文 | 不保存，待決需求 | 不適用 | 若未來要保存，須另開 Issue。 |
| OpenAI、Gemini、雲端 OCR 長期 API key | 不保存 | 不適用 | 只可在受控後端保存。 |

## 3. 邏輯資料模型

### 3.1 contact_records

每一筆代表使用者完成的一張結構化聯絡人記錄。

| 欄位 | 目的 |
| --- | --- |
| contact_id | 穩定 UUID 主鍵，不依賴 Android Contacts 的識別碼。 |
| display_name | 姓名或使用者指定的顯示名稱。 |
| company | 公司。 |
| job_title | 職稱。 |
| note | 使用者確認的備註。 |
| created_at / updated_at | 建立與修改時間。 |
| completion_source | Manual、OfflineOcr、CloudOcr、Ai 或混合來源的可讀摘要。 |
| contacts_export_state | NotRequested、Pending、Completed、Cancelled、Failed。 |
| android_contact_id | 可為空；只有 Android Contacts 實際回傳可追蹤結果時才保存。 |

### 3.2 contact_values

多筆值使用獨立子表，避免把多個電話或 Email 串在同一欄。

| 欄位 | 目的 |
| --- | --- |
| value_id | 子資料 UUID 主鍵。 |
| contact_id | 指向 contact_records。 |
| value_kind | Phone、Email、Website、Address、Other。 |
| value | 使用者確認的顯示值。 |
| normalized_value | 用於查詢與去除格式差異的值，例如標準化電話。 |
| label | Home、Work、Mobile 或使用者自訂標籤。 |
| ordinal | 同類多筆值的呈現順序。 |
| source_mode | Manual、OfflineOcr、CloudOcr、Ai。 |
| locked_at | 使用者鎖定該值的時間；不保存原始 OCR 全文。 |

### 3.3 索引與查詢準備

第一版至少建立下列索引：

- contact_records 的 display_name、company、updated_at。
- contact_values 的 contact_id、value_kind、normalized_value。
- contacts_export_state 供查詢尚未匯出的本機聯絡人。

後續查詢系統若需要全文、模糊或跨欄位搜尋，應另開需求評估 Room FTS / FTS5；第一版不假定全文索引必需，也不直接暴露 SQLite 檔案。

## 4. 寫入、交易與一致性

完成操作必須在單一 Room 寫入交易內執行：

1. 驗證草稿中的資料類型與必要識別欄位。
2. 寫入 contact_records。
3. 寫入所有 contact_values。
4. 寫入本機完成狀態。
5. 成功後才將畫面轉為 Completed。

若任一步驟失敗，整個交易回復，App 顯示未存檔且保留記憶體草稿。Android Contacts 匯出不屬於此交易；它是交易後、可取消的外部 side effect，必須獨立更新 contacts_export_state。

## 5. 安全、保存與回復

- 資料庫放在 App 私有儲存範圍，不能用外部共享資料夾作為完成聯絡人資料庫。
- 不儲存名片影像、即時影格、完整 OCR 原文或供應商 API key，以最小化資料風險。
- Room schema 每次修改必須遞增版本、提供 migration，並測試升級與必要回復情境。
- 刪除、保留期限、資料庫額外加密及 Android Auto Backup 尚未定案。這些是資料安全邊界，不得在實作時自行假設。
- 後續查詢系統僅能經 Repository／DAO 取得最小欄位；不提供任意 SQL、資料庫檔下載或完整 dump。

## 6. 最小驗證計畫

| 情境 | 通過條件 |
| --- | --- |
| 單筆完成聯絡人 | App 重啟後可查到姓名、公司、職稱與已確認值。 |
| 多筆電話／Email | 全部子欄位保留順序與類型，不互相覆寫。 |
| 交易故障 | 模擬第二步或第三步失敗後，查不到半筆 contact_records 或孤兒 contact_values。 |
| Contacts 匯出取消 | 本機 SQLite 記錄仍存在，狀態為 Cancelled 或等效可讀結果。 |
| 原始資料最小化 | 資料庫中找不到名片影像路徑、完整 OCR 原文或長期 API key。 |
| Query Module | 依姓名、公司、電話或 Email 的標準化值可在 App 資料存取層查詢。 |
| Schema 升級 | 每次 Room migration 有測試資料與升級結果驗證。 |

## 7. 開發前阻擋與待決事項

| 待決事項 | 是否阻擋 | 原因 |
| --- | --- | --- |
| D-02：是否保存影像或完整 OCR 原文 | 是 | 改變敏感資料、表結構與刪除策略。 |
| D-06：資料庫加密與 Auto Backup | 是 | 決定本機機密與裝置遷移風險。 |
| 查詢 UI 的詳細功能 | 否，可後續新增 | 現有結構與索引先支援基礎欄位查詢。 |
| 多裝置同步、分享與匯出 | 是，若新增 | 會改變資料安全、衝突與備份設計。 |

## 8. 參考資料

- [Android：以 SQLite 儲存資料](https://developer.android.com/training/data-storage/sqlite)
- [Android：以 Room 建立本機資料庫](https://developer.android.com/training/data-storage/room)
- [Android 資料儲存概覽](https://developer.android.com/training/data-storage)
