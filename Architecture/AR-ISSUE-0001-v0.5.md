# Android 名片掃描系統 Google Sheets 匯出架構增補 v0.5

| 項目 | 內容 |
| --- | --- |
| Issue ID | ISSUE-0001 |
| 文件狀態 | SA／系統整合草案；使用者授權式匯出已納入，未建立 Android 專案、Google Cloud 專案或實際連線。 |
| 版本 | 0.5.0-draft |
| 更新日期 | 2026-09-23 |
| 基準文件 | `Architecture/AR-ISSUE-0001-v0.4.md`；除本文件明確變更者外，其餘架構維持有效。 |
| 本版決策 | Google Sheets 為使用者授權的外部 append 輸出；Room / SQLite 仍是本機主資料來源。 |

## 1. 架構結論

新增一條與雲端 OCR／AI、Android Backup 和 Android Contacts 匯出彼此獨立的資料外送路徑：

`Ready Contact Repository → Export Preview & Controller → Google OAuth / Picker → Google Sheets API append → Local Export Journal`

此路徑只會在使用者按下上傳並確認預覽後啟動。Google 登入負責帳號選擇；Google OAuth 授權負責試算表存取，兩者不得混為同一個「已登入即可上傳」狀態。

### 1.1 目標識別，不使用檔案系統路徑

Google Sheets API 寫入以 `spreadsheetId` 與 A1 `range` 為契約。Google Sheet 網址僅是使用者輸入／顯示形式，App 解析後須儲存：

- `spreadsheet_id`：目標試算表檔案 ID。
- `sheet_title`：目標工作表分頁名稱。
- `append_range`：受控 A1 範圍，例如 `名片資料!A:Z`。
- `binding_mode`：`Picker` 或 `UrlValidated`。
- 欄位映射版本與使用者可辨識的安全顯示名稱／網址。

OAuth 權限作用在整份試算表檔案，不能縮小到單一分頁。分頁與範圍因此是 App 的寫入限制與使用者確認資訊，而非 Google 權限本身；若同一份試算表有其他敏感分頁，使用者／管理者應以 Google Sheets 的保護範圍與共用設定管理。

## 2. 元件與責任

| 元件 | 新增責任 | 明確不負責 |
| --- | --- | --- |
| Google Sheets Settings UI | 啟動帳號授權、選取或驗證目標、設定分頁／範圍／欄位映射、顯示連線狀態。 | 保存 Google 密碼或把登入視為自動同步同意。 |
| Google Authorization Adapter | 以 Android 對應的官方 OAuth 授權元件取得短期、最小範圍存取；處理帳號選擇、取消、撤銷、過期。 | 把 OAuth token 寫入 Room、檔案、日誌或 Backup。 |
| Google Picker Adapter | 以使用者互動選取 Google Sheet，取回受授權檔案 ID。 | 瀏覽或管理整個 Drive、建立資料夾或上傳名片影像。 |
| Sheet Binding Repository | 保存目標 ID、分頁、範圍、映射、帳號顯示資訊雜湊／最小識別與連線狀態。 | 保存 access／refresh token 或任意帳號密碼。 |
| Export Preview & Controller | 從 `Ready` 聯絡人產生輸出列、讓使用者確認、建立唯一 export event、調用 append、歸類結果。 | 未經確認背景上傳、讀回／合併 Sheet、修改既有列。 |
| Google Sheets API Adapter | 呼叫 `spreadsheets.values.append`，指定 `spreadsheetId`、A1 range 及明確輸入選項；回傳更新範圍供事件紀錄。 | 傳送原始名片影像、OCR 原文、憑證或 API key。 |
| Local Export Journal | 保存 append 請求的最小稽核與重試判斷資訊。 | 當成遠端成功的替代證明或保存遠端整列內容。 |

## 3. 權限與信任邊界

### 3.1 建議的第一版路徑

1. 使用者開啟設定並主動選擇連結 Google 帳號。
2. App 以 Android OAuth client 身分導向官方授權流程，使用者可選帳號、閱讀權限並取消。
3. 建議使用 Google Picker 選擇單一 Google Sheets 檔案；其 Android 整合要求 `drive.file`，以縮小到使用者選擇的檔案。
4. App 只保存檔案 ID／映射等設定，不保存 OAuth 憑證。每次前景手動上傳時再取得可用授權。
5. 使用者確認列預覽後，App 直接以使用者委派授權呼叫 Sheets `append`；成功回應才把 export event 標記為 `Uploaded`。

這個手動、前景、使用者委派的模式不需要在 APK 內放置任何共享 Google service-account 金鑰，也不把使用者 Google token 交給既有的 OCR／AI Provider Gateway。Android OAuth client ID 是公開識別子，不是可以拿來保存的 client secret；Google Cloud 設定、套件名稱及簽章關聯須在實作前建立並驗證。

### 3.2 URL／ID 輸入的限制

App 可接受 Google Sheets HTTPS 網址以提供使用者熟悉的「路徑」設定方式，但只貼上網址不等於 App 已有寫入權。若要寫入未經 Picker 受權的既有試算表，通常需採 `spreadsheets` 權限；此為敏感且範圍較廣的權限。是否支援此模式由 D-09 決定，不能在未確認前假設。

無論採 Picker 或網址，App 必須在開始匯出前驗證目標可寫、工作表存在、範圍與映射相容；驗證失敗不改寫本機聯絡人或自動改用別的帳號／試算表。

### 3.3 禁止路徑

- 使用者未選擇「上傳」或未確認預覽時，`Ready` 聯絡人不得離開裝置。
- 原始名片影像不得經 Sheets、Picker、Drive 或 OCR／AI Gateway 上傳；Google Drive 影像保存須另立需求與權限評估。
- Google 授權不可作為 Android Backup、雲端 OCR、AI 或 Android Contacts 匯出的同意替代。
- 不得讓 Sheet 內容以背景拉取、雙向合併或遠端刪除方式反向改寫 Room。
- 不得在 Room、app-private files、Android Backup、Crash report 或一般 log 保存 OAuth token、refresh token、authorization code、Cookie 或密碼。

## 4. 資料流與失敗語意

| 階段 | 動作 | 持久狀態／安全語意 |
| --- | --- | --- |
| 設定 | 帳號授權、Picker／網址設定、分頁／範圍與欄位映射驗證。 | 建立 `google_sheet_bindings`；無 token、無聯絡人外送。 |
| 預覽 | 使用者針對一筆 `Ready` 聯絡人點選上傳並檢查欄位。 | 建立 `Prepared` export event；取消即結束。 |
| 呼叫 | 以使用者授權向 Sheets API append 一列。 | 事件為 `Sending`；傳送內容為映射後欄位，不含影像。 |
| 成功 | 收到可驗證的 API 成功回應與更新範圍。 | `Uploaded`，寫入遠端更新範圍和時間。 |
| 已知失敗 | 權限、範圍、網路、驗證或 API 錯誤。 | `Failed`，保留不含名片內容的錯誤碼／類別；本機聯絡人仍是 `Ready`。 |
| 結果未知 | 逾時、斷線或收到不可判定回應。 | `OutcomeUnknown`；不得自動重送。使用者先檢視 Sheet／事件後才可明確產生新的 export event。 |

Sheets `append` 會在指定範圍找到表格並把值新增到下一列，因此第一版不假設遠端列號固定或可安全覆寫。每列包含不可變的 `app_export_id`，作為使用者查找與人工判斷是否重複的依據。

## 5. 備份、還原與維運邊界

- Android Backup 可以依既有 v0.4 allowlist 備份已完成的 Room 資料與名片影像；Google Sheet binding 的無機密設定是否納入，實作時須以 allowlist 具體判定。
- 即使 binding 被還原，App 也不得帶著可用的 OAuth token 還原；首次上傳前必須重新授權並重新驗證目標試算表。
- 本機匯出事件可保存於 Room 作為追蹤資料，但不等於 Google Sheet 的備份、也不代表遠端永久保存。
- 在本機有網路不可用、Google API 停止服務、OAuth consent 未完成、權限被撤銷或試算表共用權變更時，App 只顯示可操作狀態，保留本機資料，不自行改資料、切換帳號或向別的雲端傳送。
- 第一版不提供背景工作排程，因此不需承諾 Android 重開機後持續傳送或保存長期 refresh token。日後若要背景同步，須另做帳號、token、作業排程、重試、隱私、資安及維運評估。

## 6. 架構驗收與待決事項

| 項目 | 驗收／判定 |
| --- | --- |
| 授權邊界 | 靜態與整合測試證明未授權、取消、撤銷或 token 過期時沒有 Sheets 寫入；授權 scope 符合 D-09。 |
| 資料最小化 | 封包與資料模型檢查證明只有欄位映射值與 `app_export_id` 被送出；影像、raw OCR、token 和 API key 均不在請求／log。 |
| 人工確認 | 未點選上傳、未確認預覽、或連線設定尚未完成時，沒有呼叫 append。 |
| 不確定重送 | 逾時情境生成 `OutcomeUnknown`，沒有自動重送；新寫入必須使用者再次確認。 |
| Backup 邊界 | 備份／還原檢查證明 OAuth 憑證不會還原成可用狀態；使用者重新授權後才可上傳。 |

待決事項：D-09（Picker-only `drive.file` 或 URL 模式廣泛 scope）、D-10（欄位映射）、D-11（維持手動 append 或另開自動同步需求）、D-12（中斷連結與 Google 帳戶撤銷指引）。

## 7. 參考資料

- [Google Sheets API：選擇 OAuth 權限範圍](https://developers.google.com/workspace/sheets/api/scopes)
- [Google Sheets API：`spreadsheets.values.append`](https://developers.google.com/workspace/sheets/api/reference/rest/v4/spreadsheets.values/append)
- [Google Picker：Android／行動裝置整合](https://developers.google.com/workspace/drive/picker/guides/desktop-mobile-picker)
- [Google Workspace：設定 OAuth 同意畫面](https://developers.google.com/workspace/guides/configure-oauth-consent)
