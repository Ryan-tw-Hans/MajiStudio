# Android 名片掃描與聯絡人建立系統需求規格增補 v0.5

| 項目 | 內容 |
| --- | --- |
| Issue ID | ISSUE-0001 |
| 文件狀態 | PM 草案；已納入 Google Sheets 使用者授權與匯出需求，仍待權限路徑及同步模式確認後才可實作。 |
| 版本 | 0.5.0-draft |
| 更新日期 | 2026-09-23 |
| 基準文件 | `Requirements/RS-ISSUE-0001-v0.4.md`；除本文件明確變更者外，其餘需求維持有效。 |
| 本版決策 | 使用者可連結自己的 Google 帳號、設定目標 Google 試算表，並把確認完成的結構化聯絡人資料上傳。 |

## 1. 範圍與名詞釐清

Google Sheets 不是 Android 裝置可直接指定的檔案系統「路徑」。本需求中的「Google Sheets 路徑」統一定義為下列可驗證目標：

1. 使用者貼上的 Google 試算表 HTTPS 網址，解析為 `spreadsheetId`；或
2. 使用 Google Picker 選取的 Google 試算表檔案 ID。

一個匯出目標還必須包含工作表分頁名稱與 A1 起始範圍（例如 `名片資料!A:Z`）。App 顯示網址供使用者辨識，但持久設定以受控的 `spreadsheetId`、工作表名稱與範圍為準；不得接受本機絕對路徑、任意 Drive 資料夾路徑或未驗證 URI。

本版把 Google Sheets 定位為「使用者主動匯出的外部副本」，不是本機 Room / SQLite 的取代品、不是 Android Backup、不是雲端 OCR／AI 服務，也不是雙向同步主資料庫。

## 2. 新增功能需求

| ID | 新增或修訂需求 |
| --- | --- |
| FR-26 | App 必須提供「連結 Google 帳號」動作，使用 Google OAuth 2.0 授權流程，顯示帳號選擇、要求的權限與取消結果。僅 Google 登入身分不足以取得試算表寫入權限；必須另取得明確授權。 |
| FR-27 | App 必須讓使用者以 Google Picker 選取目標試算表，或貼上 Google 試算表網址以設定目標；設定完成前必須解析並驗證 `spreadsheetId`、工作表分頁與可附加資料的 A1 範圍。 |
| FR-28 | App 必須顯示欄位對應預覽，至少包含 App 匯出識別碼、姓名、公司、職稱、手機、電話、電子郵件、地址、網站、備註、建立／更新時間；使用者可停用非必要欄位或把欄位對應到目標表頭。 |
| FR-29 | 第一版預設由使用者在一筆 `Ready` 聯絡人詳情頁點選「上傳到 Google Sheets」後才執行。上傳前必須顯示目標 Google 帳號、試算表名稱／網址、工作表分頁與實際將送出的欄位值；取消不得產生遠端寫入。 |
| FR-30 | 成功上傳採附加新列（append-only）；不得自動修改、刪除、排序、格式化既有遠端欄位，亦不得自動將 Google Sheets 內容回寫本機資料。聯絡人後續變更時，使用者可另行確認產生新的匯出列。 |
| FR-31 | 匯出資料只限已完成的結構化聯絡人欄位及最小必要的匯出追蹤欄位。原始名片影像、完整 OCR 原文、即時影格、AI／OCR API 機密、診斷日誌與 Android Backup 資料不得上傳至 Google Sheets。 |
| FR-32 | App 必須在本機建立每次匯出的不可變事件紀錄，包含本機匯出 ID、聯絡人版本、目標 binding、請求時間、結果、遠端回應的範圍（如有）及不含名片內容的錯誤分類。發生網路中斷、逾時或未知結果時，不得靜默重送而造成重複列。 |
| FR-33 | 使用者可查看連結帳號與目標試算表、手動中斷連結或更換目標。中斷連結後必須停止後續上傳、清除可撤銷的授權狀態，保留本機聯絡人與既有匯出稽核紀錄。 |
| FR-34 | Google 授權被取消、帳號失效、目標試算表不存在、沒有寫入權或表頭／範圍不相容時，App 必須以可理解訊息停止本次上傳並保留本機 `Ready` 資料；不得降級改傳到其他雲端服務。 |

## 3. 資料外送與權限契約

| 操作 | 使用者可見同意 | 可送出的資料 | 不可送出的資料 |
| --- | --- | --- | --- |
| 連結帳號／選擇試算表 | Google 帳號與 OAuth 權限畫面；可取消。 | OAuth 所需的最小帳號／授權資訊。 | 名片內容、影像、OCR 原文。 |
| 設定目標 | 顯示所選試算表、分頁、範圍和欄位映射。 | 試算表識別資訊與本機設定。 | Google 密碼、長期 OAuth token。 |
| 手動上傳 | 上傳前再次顯示目標與欄位預覽。 | 已確認的結構化欄位和 `app_export_id`。 | 原始名片影像、OCR 原文、暫存影格、API key。 |
| Android Backup／換機 | 依 v0.4 已確認的系統政策。 | 已完成的本機資料與 Ready 名片影像。 | Google OAuth token、連線暫存、未完成匯出狀態。 |

### 3.1 權限最小化

- 優先方案為 Google Picker 選擇檔案後，以 `drive.file` 取得僅限使用者選取檔案的存取權。
- 若產品要支援「只貼網址、不經 Picker」的既有試算表寫入，可能需要 `spreadsheets` 權限；此權限範圍較廣，必須在實作前取得 D-09 確認、完成 OAuth consent 設定與對外發布所需的 Google 審查。
- 不得要求 `drive` 或 `drive.readonly` 等廣泛 Drive 權限，也不得為了取得帳號 Email 而把登入識別資料混入名片欄位。
- OAuth access token、refresh token、授權碼、Cookie、密碼及任何可重放憑證不得寫入 Room／SQLite、名片影像目錄、日誌或 Android Backup allowlist。

## 4. 使用流程

1. 使用者在「Google Sheets 匯出」設定頁選擇「連結 Google 帳號」。
2. App 以系統瀏覽器／官方授權元件完成帳號選擇與最小權限同意；取消時回到設定頁，不建立可用 binding。
3. 使用者用 Google Picker 選擇試算表，或貼上網址並依採用的權限策略完成驗證；再選擇分頁、範圍與欄位映射。
4. App 驗證目標可用後儲存本機 binding；這不代表已上傳任何聯絡人。
5. 使用者在 `Ready` 聯絡人詳情頁選擇「上傳到 Google Sheets」，檢查預覽後明確確認。
6. App 以 `append` 寫入一列，接收成功／失敗／不確定結果並記錄本機匯出事件。
7. 只有可驗證成功才顯示「已新增到 [目標試算表]」；不確定結果顯示「結果未知，請先查看匯出紀錄與試算表」，由使用者決定是否重新上傳。

## 5. 非功能需求與驗收補充

| ID | 要求 | 通過標準 |
| --- | --- | --- |
| NFR-13 | 權限可理解性 | 使用者能區分 Google 登入、Google Sheets 寫入授權、Android Backup 與雲端 OCR／AI；取消或拒絕授權不會影響本機保存。 |
| NFR-14 | 最小資料外送 | 封包／整合測試證明只在使用者確認上傳後送出映射欄位，不含影像、OCR 原文、token 或 API key。 |
| NFR-15 | 遠端寫入可追溯性 | 每次成功、失敗與結果未知的請求均有本機 export event；重試不會在未知結果下自動新增重複列。 |
| NFR-16 | 權限生命週期 | 中斷連結、權限撤銷、還原／換機後重授權的行為可測；token 不出現在 SQLite、備份、日誌或診斷輸出。 |
| NFR-17 | 遠端資料邊界 | 試算表被視為外部使用者資料；第一版只 append 指定範圍，不進行讀回、雙向合併、影像上傳或背景排程同步。 |

## 6. 決策狀態與不在本版範圍

| ID | 狀態 | 結論／待決事項 |
| --- | --- | --- |
| D-09 | 待確認 | 目標選取是否只採 Google Picker + `drive.file`（建議），或同時支援網址直接連線並請求 `spreadsheets` 權限。 |
| D-10 | 待確認 | 預設工作表表頭與欄位映射是否採本文件列出的 12 欄；是否允許使用者重新命名／自訂每一欄。 |
| D-11 | 暫定 | 第一版為單筆、使用者確認後的 append；自動同步、背景重試、批次匯出與遠端更新不在本版範圍。 |
| D-12 | 待確認 | Google 授權被撤銷或使用者中斷連結時，是否只移除本機 binding，或另提供前往 Google 帳戶權限頁的指引。 |
| D-13 | 不在本版範圍 | 原始名片影像上傳 Google Drive、在 Sheets 內嵌影像、從 Sheet 反向同步、多人協作衝突解決與以 Google Sheet 作為主資料庫。 |

既有 D-06-A、D-06-B、D-07、D-08 的結論維持不變。Google Sheets 匯出不因 Android Backup 已允許而獲得資料外送權限。

## 7. 參考資料

- [Google Sheets API：選擇 OAuth 權限範圍](https://developers.google.com/workspace/sheets/api/scopes)
- [Google Sheets API：`spreadsheets.values.append`](https://developers.google.com/workspace/sheets/api/reference/rest/v4/spreadsheets.values/append)
- [Google Picker：Android／行動裝置整合](https://developers.google.com/workspace/drive/picker/guides/desktop-mobile-picker)
- [Google Workspace：設定 OAuth 同意畫面](https://developers.google.com/workspace/guides/configure-oauth-consent)
