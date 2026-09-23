# 資料庫與保存增補：ISSUE-0001 Android Backup／裝置轉移 v0.3

| 項目 | 內容 |
| --- | --- |
| Issue ID | ISSUE-0001 |
| 文件狀態 | DBA 草案；備份決策已確認，額外加密與資料生命週期仍待確認。 |
| 版本 | 0.3.0-draft |
| 更新日期 | 2026-09-23 |
| 基準文件 | `DB/DBA-ISSUE-0001-v0.2.md`；除本文件明確變更者外，其餘 v0.2 資料模型與定稿規則維持有效。 |
| 本版決策 | 允許 Ready 原始名片影像、Room / SQLite 結構化聯絡人與影像 metadata 納入 Android Auto Backup 及 device-to-device transfer。 |

## 1. DBA 結論

採「明確 allowlist 的 Android Backup」：

- Room / SQLite 是聯絡人、欄位、影像 metadata 與狀態的結構化保存來源。
- `filesDir/card-images/` 只保存 Ready 原始影像，並與 `card_image_assets` 的 SHA-256、相對路徑及狀態對應。
- `.pending/`、cache、完整 OCR 原文、預覽影格、診斷檔與機密排除於備份外。
- 因備份可能在保存狀態間擷取資料，還原後必須先跑既有 Recovery，不能以資料庫 row 存在就判定影像完整。

此結論只代表允許 Android 的系統備份及轉移，不選擇或預設 App 層資料庫／影像額外加密。

## 2. 可備份資料清冊

| 資料 | Cloud backup | Device transfer | 資料處理規則 |
| --- | --- | --- | --- |
| Room / SQLite 正式資料庫 | 是 | 是 | 納入資料庫 domain；完成實作後須驗證 database、journal／WAL 及 schema migration 的還原一致性。 |
| `contact_records`、`contact_values`、`card_image_assets` | 是，透過資料庫 | 是，透過資料庫 | 包含 Ready 資料及必要 metadata；不把 raw OCR 新增進 schema。 |
| `filesDir/card-images/` Ready 原始影像 | 是 | 是 | 納入 file domain；以不含個資的檔名與 SHA-256 對應。 |
| `filesDir/card-images/.pending/` | 否 | 否 | 強制排除，即使上層 image 目錄被 include。 |
| cache、code cache、暫存預覽、裁切衍生影像 | 否 | 否 | 不應進入完成資料、也不應備份。 |
| log、診斷輸出、錯誤報告 | 否 | 否 | 只能保留不含名片內容及憑證的最低診斷資訊。 |
| API key、登入 Cookie、ChatGPT／Gemini 憑證 | 否 | 否 | 這些資料不應存在於 App 持久層。 |

## 3. 備份設定與資料庫契約

1. 實作時以 Android 官方 backup 設定建立 cloud backup 與 device transfer 的明確 include／exclude，不可只使用預設值。
2. 完成前必須選定 Room database 的正式檔名，並確認 backup 設定可一致還原該資料庫與必要 journal 檔；不能只備份單一主檔就宣稱資料庫一致。
3. `card_image_assets.relative_path` 一律相對於 `filesDir/card-images/`。還原後不得接受備份內容以外的絕對路徑、外部 URI 或 `..` traversal。
4. `card_image_assets.sha256` 是還原完整性檢查依據。雜湊不符時，記錄 `ImageMissing` 或 Failed 狀態，保留可用的結構化資料。
5. `PendingFinalize` 與 `PendingDelete` 不是一般查詢可見資料。還原後 Recovery 先處理這些狀態，再由 DAO 開放 Ready 結果。
6. 自動備份不可作為資料庫 migration、刪除操作或手動匯出的取代品；schema 改版仍需 Room migration 與升級測試。

## 4. 還原與完整性流程

| 順序 | 動作 | 成功條件 |
| --- | --- | --- |
| 1 | 開啟資料庫、檢查 Room schema 與 migration。 | 可讀取資料庫，無破壞性自動重建。 |
| 2 | 取得 `Ready`／Pending 的聯絡人和 `card_image_assets` metadata。 | 可建立有限候選集，無任意路徑存取。 |
| 3 | 以 allowlist 解析相對路徑，檢查原始影像是否存在、大小／MIME 是否合理，再計算 SHA-256。 | 只有核對成功的資產標記 Ready。 |
| 4 | 收斂 Pending／Missing／Failed 狀態。 | 一般查詢不見半筆完成資料；結構化資料不因影像缺失被靜默刪除。 |
| 5 | 顯示還原摘要與需要使用者處理的項目。 | UI 不把 Backup 成功、Contacts 匯出與 AI 傳送混為同一結果。 |

## 5. 容量與保留的 DBA 建議

- 以 `SUM(card_image_assets.byte_count WHERE storage_state='Ready')` 估算 App 已保存影像量，僅展示容量統計，不把影像內容寫入診斷資料。
- Android Auto Backup 有平台管理的每使用者容量限制；系統無法完成備份不應改變 App 本機 Ready 狀態。
- 實作前 D-08 必須決定影像最大解析度、大小與是否保留相機原始品質；此決定會直接影響本機容量與備份成功率。
- D-07 必須決定聯絡人刪除時的影像同步刪除及是否有復原窗口；刪除流程仍需採 `PendingDelete`，不可依未驗證路徑遞迴清除。

## 6. 驗收與測試要求

| 情境 | 通過條件 |
| --- | --- |
| Backup allowlist | 靜態檢查證明僅 Room 資料庫和 Ready 原始影像被包含；`.pending/`、cache、原始 OCR、日誌及機密被排除。 |
| Cloud backup 還原 | 在目標 Android 版本完成系統還原後，Ready 聯絡人可讀，影像與 SHA-256 一致。 |
| Device transfer | 換機後同樣完成 Recovery，且不把 D2D 成功誤稱為 Android Contacts 匯出。 |
| 資料庫還原一致性 | 跨 Room schema migration 或 journal 狀態測試，不能因還原而產生孤兒 metadata 或遺失 Ready 欄位。 |
| 不完整還原 | 移除／竄改一張影像，結果是 `ImageMissing` 與可讀的結構化資料，不是假成功或靜默刪除。 |
| 額度與容量 | 大量高解析影像時顯示可理解容量狀態，不自行壓縮、刪除或改變保存策略。 |

## 7. 決策狀態

| 待決事項 | 是否阻擋 | 原因 |
| --- | --- | --- |
| D-06-A：Android Auto Backup／device transfer | 已確認 | 可納入實作及測試範圍。 |
| D-06-B：App 層額外加密與金鑰生命週期 | 是 | 影像與聯絡人資料屬敏感資料，需決定額外保護與還原行為。 |
| D-07：影像保留期限與聯絡人刪除同步刪除 | 是 | 決定容量、隱私與受控刪除。 |
| D-08：原始影像最大解析度、大小與壓縮策略 | 是 | 決定備份額度、本機容量、網路與使用者期待。 |
| 雙面名片 | 是，若納入第一版 | 會把一對一影像關係改為一對多。 |

## 8. 參考資料

- [Android：Auto Backup 使用者資料](https://developer.android.com/identity/data/autobackup)
- [Android：資料備份概覽](https://developer.android.com/identity/data/backup)
- [Android：存取 App 專屬檔案](https://developer.android.com/training/data-storage/app-specific)
- [Android：以 Room 建立本機資料庫](https://developer.android.com/training/data-storage/room)
