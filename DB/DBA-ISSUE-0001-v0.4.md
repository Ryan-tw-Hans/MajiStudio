# Android 名片掃描系統 Google Sheets 匯出資料庫影響評估 v0.4

| 項目 | 內容 |
| --- | --- |
| Issue ID | ISSUE-0001 |
| 文件狀態 | DBA 草案；定義本機 binding 與匯出事件模型，未建立 Room migration。 |
| 版本 | 0.4.0-draft |
| 更新日期 | 2026-09-23 |
| 基準文件 | `DB/DBA-ISSUE-0001-v0.3.md`；除本文件明確變更者外，其餘資料模型與備份規則維持有效。 |
| 本版結論 | Google Sheets 是使用者授權的 append 輸出；本機 Room／SQLite 持續是 canonical store。 |

## 1. DBA 結論與資料邊界

新增兩個本機資料集合：目標試算表 binding 與 append 匯出事件。兩者只支援使用者明確設定與追蹤；不保存 Google 帳號密碼、OAuth access token、refresh token、authorization code、Cookie、原始名片影像或完整 OCR 原文。

遠端 Google Sheet 不可取代下列既有資料的主責來源：

- `contact_records`、`contact_values`、`card_image_assets` 仍保存在 Room／SQLite。
- `filesDir/card-images/` 的原始名片影像仍由本機資產生命週期管理。
- Google Sheet 的每一列為外部輸出快照，不做反向讀取、合併、刪除或以它覆蓋本機欄位。

## 2. 建議 Room 實體

### 2.1 `google_sheet_bindings`

| 欄位 | 建議型別／約束 | 說明 |
| --- | --- | --- |
| `binding_id` | TEXT PK（UUID） | 本機目標識別。 |
| `provider` | TEXT CHECK = `google_sheets` | 預留多供應商辨識，不保存 API endpoint。 |
| `binding_mode` | TEXT CHECK (`Picker`, `UrlValidated`) | 記錄目標取得方式與 D-09 的適用權限策略。 |
| `google_account_subject` | TEXT nullable | OAuth subject 的最小穩定識別；不可保存 token。 |
| `account_display_hint` | TEXT nullable | 遮罩後 Email 或帳號顯示名稱；不得作為授權判斷唯一依據。 |
| `spreadsheet_id` | TEXT NOT NULL | Google Sheets API 目標 ID，不是本機檔案路徑。 |
| `spreadsheet_display_name` | TEXT nullable | 設定畫面辨識用，重新驗證時可更新。 |
| `spreadsheet_url` | TEXT nullable | 僅接受 HTTPS Google Sheets canonical URL；供使用者開啟，不作任意網路請求。 |
| `sheet_title` | TEXT NOT NULL | 工作表分頁。 |
| `append_range` | TEXT NOT NULL | 受控 A1 append 範圍。 |
| `mapping_json` | TEXT NOT NULL | 受控欄位映射與 schema version；不得含 token 或名片內容快取。 |
| `authorization_state` | TEXT | `Disconnected`、`NeedsAuthorization`、`Ready`、`Revoked`、`InvalidTarget`。 |
| `validated_at` | TEXT nullable | 最後驗證成功時間。 |
| `created_at` / `updated_at` | TEXT NOT NULL | ISO 8601，帶時區。 |

索引：唯一索引 `(provider, google_account_subject, spreadsheet_id, sheet_title, append_range)`；狀態索引 `(authorization_state, updated_at DESC)`。若 Google subject 未可用，禁止以未遮罩 Email 建立多筆無法判定的 binding。

### 2.2 `google_sheet_export_events`

| 欄位 | 建議型別／約束 | 說明 |
| --- | --- | --- |
| `export_event_id` | TEXT PK（UUID） | 不可變的 `app_export_id`；同時是匯出列預設欄位。 |
| `binding_id` | TEXT FK | 指向匯出時使用的 binding。 |
| `contact_id` | TEXT FK | 指向本機 `contact_records`。 |
| `contact_revision` | INTEGER／TEXT NOT NULL | 匯出時的本機資料版本，避免把事後變更誤認成已上傳內容。 |
| `mapping_version` | TEXT NOT NULL | 可重建外送欄位意義，但不保存完整值副本。 |
| `state` | TEXT CHECK | `Prepared`、`Sending`、`Uploaded`、`Failed`、`OutcomeUnknown`、`Cancelled`。 |
| `requested_at` / `completed_at` | TEXT | ISO 8601，帶時區。 |
| `remote_updated_range` | TEXT nullable | Sheets 成功回應的更新範圍；不是遠端列主鍵保證。 |
| `http_status` | INTEGER nullable | 最小診斷資訊。 |
| `error_category` | TEXT nullable | `Network`、`Auth`、`Permission`、`Target`、`Validation`、`RateLimit`、`Unknown`；不得記錄名片欄位、token 或完整遠端回應。 |
| `retry_of_export_event_id` | TEXT nullable FK | 使用者主動再次上傳時的歷史關聯。 |

索引：`(contact_id, requested_at DESC)`、`(binding_id, requested_at DESC)`、`(state, requested_at DESC)`、`(retry_of_export_event_id)`。不對含個資欄位建立 FTS 或將匯出 payload 原樣寫進事件表。

## 3. 寫入、一致性與重試

1. 使用者開啟預覽時，先以單一 Room transaction 新增 `Prepared` event，記錄 contact revision、binding、mapping version。
2. 使用者確認且即將送出時，將 event 轉成 `Sending`；呼叫 Google API 不可包在長時間資料庫 transaction 中。
3. API 明確成功時，以短 transaction 更新為 `Uploaded` 並寫入 `remote_updated_range`；明確失敗更新為 `Failed`。
4. 網路逾時或應答不完整時更新為 `OutcomeUnknown`。因 append 不是天然冪等操作，App 禁止自動 retry；只有使用者再次確認後建立新的 event，並以 `retry_of_export_event_id` 關聯。
5. 每次外送列應含 `app_export_id`，讓使用者可在 Google Sheet 中找出事件；第一版不因表格內已有相同 ID 就自行讀回／刪除／覆寫。
6. 使用者中斷 binding 時，保存歷史 event；binding 轉為 `Disconnected`，後續不得新增 `Sending` event。可撤銷的暫存授權由授權元件清理，不寫入資料庫。

## 4. 備份、還原與刪除

- `google_sheet_bindings` 的無機密設定是否納入 Android Backup，必須在實作 backup allowlist 時逐欄評估；即使被還原，也要標示 `NeedsAuthorization` 並重新驗證，不能還原為可上傳狀態。
- `google_sheet_export_events` 屬本機稽核資料，可依資料保存政策決定是否備份；不應因重裝／還原就自動執行歷史 `Prepared`、`Sending` 或 `OutcomeUnknown` 事件。
- OAuth token、authorization code、Cookie、帳號密碼與任何密鑰均不得進入 SQLite、檔案目錄、WAL／journal、backup、測試 fixture 或 log。
- 刪除本機聯絡人時，既有匯出事件需保留可稽核的最小識別，或以受控的匿名化／保留政策處理；不得以刪除本機資料為由修改 Google Sheet 已存在的列。
- 原始名片影像維持既有 `card_image_assets` 生命週期；本次 schema 不加入影像 URI、Drive file ID 或 BLOB。

## 5. Migration 與驗收

| 情境 | 通過條件 |
| --- | --- |
| Schema migration | 從 v0.3 資料庫升級後，既有聯絡人／影像資料不變；新增 binding／event 表與索引，無 token 欄位。 |
| 目標設定 | 不合法 URL、空 ID、非 Google Sheets URL、空分頁或不受控 A1 range 不能成為 `Ready` binding。 |
| 成功 append | 產生一個 `Uploaded` event，關聯正確 `contact_id`／revision，並記錄安全的更新範圍。 |
| 結果未知 | 逾時後只有一個 `OutcomeUnknown` event，不自動新增第二列；新的上傳必須由使用者確認並建立新的 UUID。 |
| 授權撤銷／還原 | binding 轉為 `NeedsAuthorization` 或 `Revoked`，但既有聯絡人與影像可照常查詢；Room、backup、WAL 與 log 均無可用 OAuth token。 |
| 中斷連結 | 新事件被阻止，歷史事件仍可查，Google Sheet 既有列不被刪除或改寫。 |

## 6. 待決事項

| 項目 | 是否阻擋 | 原因 |
| --- | --- | --- |
| D-09：Picker + `drive.file` 或網址 + `spreadsheets` | 是 | 決定 binding mode、OAuth scope、Google Cloud consent 設定與可驗證目標流程。 |
| D-10：預設／可自訂表頭映射 | 是 | 決定 `mapping_json` schema、驗證與使用者預覽。 |
| D-11：手動 append 或自動／批次同步 | 否（採手動 append 時） | 自動模式會改變 token、排程、佇列與資料保留風險，應另立需求。 |
| D-12：中斷連結與撤銷導引 | 否 | 決定 UI／說明，但不影響資料模型的安全基線。 |

## 7. 參考資料

- [Google Sheets API：選擇 OAuth 權限範圍](https://developers.google.com/workspace/sheets/api/scopes)
- [Google Sheets API：`spreadsheets.values.append`](https://developers.google.com/workspace/sheets/api/reference/rest/v4/spreadsheets.values/append)
- [Google Picker：Android／行動裝置整合](https://developers.google.com/workspace/drive/picker/guides/desktop-mobile-picker)
