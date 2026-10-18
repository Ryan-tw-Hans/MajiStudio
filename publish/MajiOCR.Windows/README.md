# MajiOCR.Windows 0.1.0-beta.34 (資料庫同步中心排版自適應與滾動修復)

目前為本機發布版，更新日期為 2026-10-18（Asia/Taipei）。
直接執行本資料夾的 `MajiOCR.Windows.exe`。維持單一免安裝、自含執行檔（Self-Contained Single-File Executable）及離線 OCR 模型，並內嵌完整 `MajiOCR.Web` (`0.2.0-beta.1`) 靜態 PWA 資產包。

### 本版更新重點 (`0.1.0-beta.34`)
1. **修復「名片資料庫同步中心」對話框視覺跑版與按鈕裁切**：
   - WinUI 3 `ContentDialog` 預設被系統鎖定在 `MaxWidth = 548px`，導致對話框內部內容超寬時右側遭到裁切。本版解除預設上限並放寬至 `840px`，徹底修復右側【📂 瀏覽選取檔案...】按鈕被推擠出可視區域的問題。
2. **多行標題與說明文字自動換行 (TextWrapping)**：
   - 同步模式與同步方向之所有選項單選按鈕（RadioButton）升級為結構化標題與副標題排版，並啟用自適應自動換行（`TextWrapping.Wrap`），解決長文字在右側被截斷問題。
3. **支援平滑垂直滾動 (ScrollViewer)**：
   - 對話框內容全面嵌入支援垂直滾動的 `ScrollViewer`，在高 DPI 縮放或低解析度螢幕下避免內容溢出且確保完整瀏覽與操作。

---
- **FileVersion**：0.1.0.34 (`0.1.0-beta.34`)
- **SHA-256**：`958F3155EED1F763D7279D7636FEC7E546DB5BF2D2E702817C97640A0DD95CAE`
- **檔案大小**：300,000,867 位元組 (~286.1 MB)

本機單檔發布驗證與自動化測試套件（`ISSUE-0053` 7/7 PASS、`ISSUE-0052` 7/7 PASS、`ISSUE-0051` 5/5 PASS、`ISSUE-0050` 5/5 PASS、`ISSUE-0043` 5/5 PASS、`ISSUE-0038` 11/11 PASS、`ISSUE-0036` 7/7 PASS）均已全數通過。


