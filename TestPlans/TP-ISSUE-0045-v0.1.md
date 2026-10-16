# 測試計畫：修復資料欄位使用 Ctrl+V 貼上文字時誤觸發貼上圖片提示與 GitHub README Mermaid 渲染錯誤

- 文件識別碼：`TP-ISSUE-0045-v0.1`
- 關聯 Issue：`ISSUE-0045`
- 關聯規格：`RS-ISSUE-0045-v0.1`
- 負責角色：測試工程師 Agent

---

## 1. 測試範圍與目標

1. **TC-01：文字輸入欄位焦點時快捷鍵屏蔽測試**
   - 驗證當焦點在 `TextBox`、`PasswordBox`、`RichEditBox`、`AutoSuggestBox` 或其內部 Visual Tree 子元件時，`IsTextInputControl` 判定為 `true`。
   - 驗證 `KeyboardAccelerator.Invoked` 接收到快捷鍵時，若焦點為文字輸入控制項，將 `args.Handled` 標記為 `true`，且不呼叫貼上圖片處理邏輯，不彈出錯誤對話框。

2. **TC-02：非文字輸入控制項焦點時快捷鍵放行測試**
   - 驗證當焦點在 `Button`、`Canvas`、`Grid`、`ListView` 或無特定文字焦點時，`IsTextInputControl` 判定為 `false`。
   - 驗證非文字焦點下，快捷鍵正常呼叫貼上圖片邏輯。

3. **TC-03：README.md Mermaid 圖表語法驗證**
   - 驗證 `README.md` 中的 Mermaid 語法不含破壞 GitHub 解析器的複合字元，使用標準 `graph TD` 語法，標籤清晰且無未閉合符號。

4. **TC-04：版本號與發布一致性驗證**
   - 驗證 `VERSION.txt`、`CHANGELOG.txt` 與 `MajiOCR.Windows.csproj` 之版本號同步遞增至 `0.1.0.25` / `0.1.0-beta.25`。
   - 驗證可成功建置出 `publish/MajiOCR.Windows/MajiOCR.Windows.exe` 單檔執行檔。
