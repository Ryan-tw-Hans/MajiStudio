# 架構增補：ISSUE-0001 Android Auto Backup 與裝置轉移 v0.4

| 項目 | 內容 |
| --- | --- |
| Issue ID | ISSUE-0001 |
| 文件狀態 | SA 草案；已納入使用者確認的備份政策，仍待加密與保存生命週期決策後才可實作。 |
| 版本 | 0.4.0-draft |
| 更新日期 | 2026-09-23 |
| 基準文件 | `Architecture/AR-ISSUE-0001-v0.3.md`；除本文件明確變更者外，其餘 v0.3 架構維持有效。 |
| 本版決策 | 允許 App 私有原始影像與完成後的 Room / SQLite 聯絡人資料納入 Android Auto Backup 與裝置轉移。 |

## 1. 架構決策

Android Backup 是由作業系統管理的資料擷取與還原路徑，而不是 App 直接呼叫的雲端 API。因此本系統仍維持：

- 原始名片影像在 `filesDir/card-images/`，結構化資料及影像 metadata 在 Room / SQLite。
- App 不直接把備份資料 POST 到任何服務，也不會因允許 Backup 而取得 OpenAI、Gemini 或雲端 OCR 的資料傳送權限。
- 備份設定必須明確列出可備份資料及必須排除資料，而不是由儲存位置間接決定。

本版確定採取：

1. Cloud backup 與 device-to-device transfer 都允許已完成的聯絡人、欄位、影像 metadata 和 `Ready` 原始影像。
2. `.pending/`、快取、OCR 中間資料、完整 OCR 原文、日誌、診斷檔、外部 URI、任何長期 API key 均不可進入備份資料集。
3. 還原完成前先執行資料庫、資產狀態與 SHA-256 Recovery；正常查詢只呈現可驗證的 Ready 資料。

## 2. 備份元件與資料流

### 2.1 元件責任增補

| 元件 | 新增責任 | 不可承諾的事項 |
| --- | --- | --- |
| Backup Policy Configuration | 在 Android 資源及 manifest 中明確定義 cloud backup、device transfer 的 include／exclude 規則，並依目標 SDK 提供相容設定。 | 不得以「儲存在內部空間」推論全部資料都可安全備份。 |
| Android Backup / Device Transfer | 由平台在適當時機擷取及還原 allowlist 資料。 | App 不控制排程、不保證立即上傳、不保證每次還原均完整。 |
| Contact Repository + Asset Recovery | App 啟動時驗證 Room 記錄與影像資產，收斂 Pending、Missing 或可驗證的 Ready 狀態。 | 不得把未驗證資料視為完成，也不得靜默刪除使用者資料。 |
| Local Query Module | 只顯示可安全讀取的聯絡人及影像狀態，並提供不完整還原的明確訊息。 | 不得把 Android Backup、系統 Contacts 或 AI 服務的狀態混為同一結果。 |

### 2.2 備份 allowlist 契約

| 資料類別 | Cloud backup | Device transfer | 設計要求 |
| --- | --- | --- | --- |
| Room / SQLite 正式資料庫 | 允許 | 允許 | 以資料庫 domain 明確包含實際 Room database 所需的持久資料；實作後須於目標 Android 版本驗證資料庫與 journal 的還原一致性。 |
| `filesDir/card-images/` 的 Ready 原始影像 | 允許 | 允許 | 以 file domain 明確包含，並排除 `.pending/` 子目錄。 |
| `.pending/`、cache、log、OCR 暫存、原始 OCR 全文 | 禁止 | 禁止 | 明確 exclude；不得因上層 include 而被帶入。 |
| API key、登入憑證、Gateway 機密 | 禁止 | 禁止 | 長期機密本來就不得存在 App 資料層。 |
| Android Contacts 匯出後的系統聯絡人本體 | 不由本 App 控制 | 不由本 App 控制 | 本 App 只備份自己的匯出狀態，不主張控制 Contacts Provider 的備份。 |

Android 12 及以上的實作應採用對應的資料擷取規則；較舊相容範圍需採用對應的 full-backup-content 規則。因本專案尚未選定最低 Android 版本或建立 Android 專案，本文件只定義契約，未建立 manifest 或 XML。

## 3. 還原與一致性契約

1. 由 Android 平台完成 cloud restore 或 device transfer 後，App 第一次開啟必須先執行 Recovery。
2. Recovery 檢查每筆 `Ready` 聯絡人是否有 `card_image_assets` metadata、受控相對路徑與可讀原始影像；影像可讀時核對 SHA-256。
3. 資料庫有記錄但影像缺失或不符時，保留結構化聯絡人並標記 `ImageMissing`；不可把該筆資料宣稱為完整備份還原。
4. `.pending/` 不在備份資料集。若還原出 Pending database 狀態而沒有可驗證影像，Recovery 必須安全收斂為 Failed／Missing，並在一般查詢中隱藏未完成的保存工作。
5. 還原成功與「已新增 Android 系統聯絡人」是兩件事；Android Contacts 匯出狀態仍要獨立呈現。

## 4. 隱私、容量與可觀測性

- 第一次使用與設定頁必須告知原始名片影像和聯絡人資料可納入 Android Backup 與換機轉移；文案不得誤稱 App 直接上傳或即時同步。
- Android Auto Backup 的每位使用者資料額度有限；原始影像容易造成容量壓力。容量不足時，App 只能提示和提供管理方式，不能未告知就壓縮、刪除或停止保存完成影像。
- App 可記錄不含名片內容的備份／還原狀態與錯誤分類；日誌不得包含原始影像、原始 OCR 全文、資料庫內容或憑證。
- D-06-B 的 App 層額外加密仍未決。Android 平台對備份資料的保護不等於本產品已完成專屬加密、金鑰輪替或企業合規設計。

## 5. 風險、回復與驗證

| 情境 | 緩解方式 | 驗證 |
| --- | --- | --- |
| allowlist 過寬 | 明確 include 完成資料，並對 `.pending/`、cache、日誌及機密設 exclude。 | 靜態檢查 backup XML／manifest 與實機備份資料集。 |
| 還原取得資料庫但缺少影像 | Recovery 以 SHA-256 與狀態收斂為 Ready／ImageMissing。 | 備份後故意移除或竄改影像，再測試還原與查詢。 |
| Pending 記錄被還原 | `.pending/` 排除；啟動 Recovery 不讓其出現在一般查詢。 | 在定稿中斷後建立備份／還原測試。 |
| 備份額度不足 | 顯示可理解容量狀態與資料管理入口；不靜默刪除。 | 多張高解析名片壓力測試，記錄平台可觀測結果。 |
| 使用者誤解 Backup 等於 AI 上傳 | 分開命名、設定與同意文案。 | UX／可用性測試確認能區分 Android Backup、雲端 OCR、AI 與 Contacts。 |
| App 層加密未決 | 保留 D-06-B，不能宣稱已完成額外加密。 | 決策完成後再測試加密、金鑰遺失與還原。 |

## 6. 架構圖與後續決策

本版架構圖為 `Architecture/diagrams/AR-ISSUE-0001-system-architecture-v0.4.html`。為維持九節點總覽，圖中將 Gateway 與雲端 OCR／AI 合併為可選雲端服務節點，並新增 Android Backup／裝置轉移節點；供應商路由細節仍由 v0.3 架構文件定義。

| 待決項目 | 狀態 | 後續影響 |
| --- | --- | --- |
| D-06-A：允許 Backup 與裝置轉移 | 已確認 | 可納入實作範圍。 |
| D-06-B：App 層資料庫與影像額外加密 | 待確認 | 金鑰、備份、還原與測試策略。 |
| D-07：影像保存期限與刪除聯絡人時是否同步刪除 | 待確認 | 生命週期、容量與資料管理 UI。 |
| D-08：影像最大解析度、大小與壓縮策略 | 待確認 | 備份額度、裝置容量與 UX。 |

## 7. 參考資料

- [Android：Auto Backup 使用者資料](https://developer.android.com/identity/data/autobackup)
- [Android：資料備份概覽](https://developer.android.com/identity/data/backup)
- [Android：存取 App 專屬檔案](https://developer.android.com/training/data-storage/app-specific)
- [Android：以 Room 建立本機資料庫](https://developer.android.com/training/data-storage/room)
