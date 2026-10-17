using System;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows;

/// <summary>
/// UI ContentDialog implementations for Master Password setup, unlocking, changing, and database encryption.
/// Conforms to RS-ISSUE-0038, AR-ISSUE-0038, DBA-ISSUE-0038, and RS-ISSUE-0039.
/// </summary>
public static class SecurityDialogs
{
    /// <summary>
    /// Prompts user to enter master password to unlock an encrypted database on startup.
    /// Supports Enter key validation to unlock immediately without exiting the app (RS-ISSUE-0039).
    /// </summary>
    public static async Task<string?> ShowUnlockDialogAsync(XamlRoot xamlRoot, string dbPath, IAppSettingsService settingsService)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "🔐 名片管理資料庫已加密",
            PrimaryButtonText = "解鎖",
            CloseButtonText = "結束程式",
            DefaultButton = ContentDialogButton.Primary
        };

        var stack = new StackPanel { Spacing = 12, Width = 380 };
        var introText = new TextBlock
        {
            Text = "本機名片資料庫已啟用 SQLCipher 加密保護。\n請輸入主密碼以解鎖您的名片與分類資料：",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };

        var pwdBox = new PasswordBox
        {
            PlaceholderText = "請輸入主密碼",
            PasswordChar = "●"
        };

        var errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Colors.IndianRed),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap
        };

        stack.Children.Add(introText);
        stack.Children.Add(pwdBox);
        stack.Children.Add(errorText);
        dialog.Content = stack;

        string? unlockedPassword = null;

        bool VerifyAndUnlock()
        {
            string entered = pwdBox.Password;
            if (string.IsNullOrEmpty(entered))
            {
                errorText.Text = "請輸入主密碼。";
                errorText.Visibility = Visibility.Visible;
                pwdBox.Focus(FocusState.Programmatic);
                return false;
            }

            var settings = settingsService.LoadSettings();
            bool verified = false;

            if (StorageService.IsDatabaseEncrypted(dbPath))
            {
                verified = StorageService.TryVerifyPassphrase(dbPath, entered, out _);
                if (verified && (string.IsNullOrEmpty(settings.PasswordHash) || string.IsNullOrEmpty(settings.PasswordSalt)))
                {
                    var (hash, salt) = PasswordSecurityService.HashPassword(entered);
                    settingsService.UpdateSecuritySettings(true, hash, salt);
                }
            }
            else if (!string.IsNullOrEmpty(settings.PasswordHash) && !string.IsNullOrEmpty(settings.PasswordSalt))
            {
                verified = PasswordSecurityService.VerifyPassword(entered, settings.PasswordHash, settings.PasswordSalt);
            }
            else
            {
                verified = true;
            }

            if (!verified)
            {
                errorText.Text = "密碼不正確，請重新輸入。";
                errorText.Visibility = Visibility.Visible;
                pwdBox.SelectAll();
                pwdBox.Focus(FocusState.Programmatic);
                return false;
            }

            unlockedPassword = entered;
            errorText.Visibility = Visibility.Collapsed;
            return true;
        }

        pwdBox.Loaded += (s, e) => pwdBox.Focus(FocusState.Programmatic);

        pwdBox.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                if (VerifyAndUnlock())
                {
                    dialog.Hide();
                }
            }
        };

        dialog.PrimaryButtonClick += (s, args) =>
        {
            if (!VerifyAndUnlock())
            {
                args.Cancel = true;
            }
        };

        await dialog.ShowAsync();
        return unlockedPassword;
    }

    /// <summary>
    /// Displays the Security & Master Password settings dashboard.
    /// </summary>
    public static async Task ShowSecuritySettingsDialogAsync(XamlRoot xamlRoot, IStorageService storageService, IAppSettingsService settingsService)
    {
        var settings = settingsService.LoadSettings();
        bool isProtected = settings.IsPasswordProtected || storageService.IsEncrypted;

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "🛡️ 安全與密碼保護設定",
            CloseButtonText = "關閉",
            DefaultButton = ContentDialogButton.Close
        };

        var stack = new StackPanel { Spacing = 16, Width = 420 };

        var statusBorder = new Border
        {
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(8),
            Background = isProtected
                ? new SolidColorBrush(ColorHelper.FromArgb(30, 0, 180, 80))
                : new SolidColorBrush(ColorHelper.FromArgb(30, 200, 150, 0)),
            BorderThickness = new Thickness(1),
            BorderBrush = isProtected
                ? new SolidColorBrush(ColorHelper.FromArgb(120, 0, 180, 80))
                : new SolidColorBrush(ColorHelper.FromArgb(120, 200, 150, 0))
        };

        var statusStack = new StackPanel { Spacing = 4 };
        var statusHeader = new TextBlock
        {
            Text = isProtected ? "🔒 已啟用資料庫加密保護" : "🔓 未啟用密碼保護 (一般模式)",
            FontWeight = FontWeights.Bold,
            FontSize = 14
        };
        var statusDesc = new TextBlock
        {
            Text = isProtected
                ? "資料庫採用 SQLCipher (AES-256) 演算法加密。\n每次開啟程式皆需驗證主密碼以解鎖名片與標籤資料。"
                : "目前名片資料庫採用標準 SQLite 格式儲存。\n開啟程式無需輸入密碼。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85
        };

        statusStack.Children.Add(statusHeader);
        statusStack.Children.Add(statusDesc);
        statusBorder.Child = statusStack;
        stack.Children.Add(statusBorder);

        var actionsStack = new StackPanel { Spacing = 10 };

        if (!isProtected)
        {
            var enableBtn = new Button
            {
                Content = "🛡️ 啟用主密碼與資料庫加密...",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Style = (Style)Application.Current.Resources["AccentButtonStyle"]
            };
            enableBtn.Click += async (s, e) =>
            {
                dialog.Hide();
                await ShowEnablePasswordDialogAsync(xamlRoot, storageService, settingsService);
            };
            actionsStack.Children.Add(enableBtn);
        }
        else
        {
            var changePwdBtn = new Button
            {
                Content = "🔑 修改主密碼...",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            changePwdBtn.Click += async (s, e) =>
            {
                dialog.Hide();
                await ShowChangePasswordDialogAsync(xamlRoot, storageService, settingsService);
            };

            var disableBtn = new Button
            {
                Content = "🔓 關閉密碼保護 (解密還原)...",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            disableBtn.Click += async (s, e) =>
            {
                dialog.Hide();
                await ShowDisablePasswordDialogAsync(xamlRoot, storageService, settingsService);
            };

            actionsStack.Children.Add(changePwdBtn);
            actionsStack.Children.Add(disableBtn);
        }

        stack.Children.Add(actionsStack);
        dialog.Content = stack;

        await dialog.ShowAsync();
    }

    /// <summary>
    /// Dialog for enabling master password and converting SQLite DB to SQLCipher AES-256.
    /// Supports Enter key navigation and submission (RS-ISSUE-0039).
    /// </summary>
    public static async Task ShowEnablePasswordDialogAsync(XamlRoot xamlRoot, IStorageService storageService, IAppSettingsService settingsService)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "🛡️ 設定主密碼並啟用加密",
            PrimaryButtonText = "確認啟用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        var stack = new StackPanel { Spacing = 12, Width = 380 };
        var intro = new TextBlock
        {
            Text = "請設定一組主密碼（至少 6 個字元）。\n啟用後系統將自動為本機名片資料庫進行 AES-256 加密。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };

        var pwd1 = new PasswordBox { PlaceholderText = "請輸入新主密碼 (至少 6 字元)" };
        var pwd2 = new PasswordBox { PlaceholderText = "請再次確認新主密碼" };
        var errText = new TextBlock
        {
            Foreground = new SolidColorBrush(Colors.IndianRed),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap
        };

        stack.Children.Add(intro);
        stack.Children.Add(pwd1);
        stack.Children.Add(pwd2);
        stack.Children.Add(errText);
        dialog.Content = stack;

        bool enableSuccess = false;

        async Task<bool> ExecuteEnableAsync()
        {
            string p1 = pwd1.Password;
            string p2 = pwd2.Password;

            if (!PasswordSecurityService.ValidatePassword(p1, out string validationErr))
            {
                errText.Text = validationErr;
                errText.Visibility = Visibility.Visible;
                pwd1.Focus(FocusState.Programmatic);
                return false;
            }

            if (p1 != p2)
            {
                errText.Text = "兩次輸入的密碼不相符，請重新確認。";
                errText.Visibility = Visibility.Visible;
                pwd2.SelectAll();
                pwd2.Focus(FocusState.Programmatic);
                return false;
            }

            try
            {
                await storageService.EnableEncryptionAsync(p1);
                var (hash, salt) = PasswordSecurityService.HashPassword(p1);
                settingsService.UpdateSecuritySettings(true, hash, salt);
                enableSuccess = true;
                return true;
            }
            catch (Exception ex)
            {
                errText.Text = $"啟用失敗：{ex.Message}";
                errText.Visibility = Visibility.Visible;
                return false;
            }
        }

        pwd1.Loaded += (s, e) => pwd1.Focus(FocusState.Programmatic);

        pwd1.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                pwd2.Focus(FocusState.Programmatic);
            }
        };

        pwd2.KeyDown += async (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                if (await ExecuteEnableAsync())
                {
                    dialog.Hide();
                }
            }
        };

        dialog.PrimaryButtonClick += async (s, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (!await ExecuteEnableAsync())
                {
                    args.Cancel = true;
                }
            }
            finally
            {
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();

        if (enableSuccess)
        {
            var tipDialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "✅ 密碼保護啟用成功",
                Content = "名片資料庫已成功升級為 SQLCipher (AES-256) 加密。\n下次啟動本軟體時，請使用設定的主密碼解鎖。",
                CloseButtonText = "確定"
            };
            await tipDialog.ShowAsync();
        }
    }

    /// <summary>
    /// Dialog for changing master password.
    /// Supports Enter key navigation and submission (RS-ISSUE-0039).
    /// </summary>
    public static async Task ShowChangePasswordDialogAsync(XamlRoot xamlRoot, IStorageService storageService, IAppSettingsService settingsService)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "🔑 修改主密碼",
            PrimaryButtonText = "確認變更",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        var stack = new StackPanel { Spacing = 12, Width = 380 };
        var oldPwd = new PasswordBox { PlaceholderText = "請輸入目前舊密碼" };
        var newPwd1 = new PasswordBox { PlaceholderText = "請輸入新主密碼 (至少 6 字元)" };
        var newPwd2 = new PasswordBox { PlaceholderText = "請再次確認新主密碼" };
        var errText = new TextBlock
        {
            Foreground = new SolidColorBrush(Colors.IndianRed),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap
        };

        stack.Children.Add(oldPwd);
        stack.Children.Add(newPwd1);
        stack.Children.Add(newPwd2);
        stack.Children.Add(errText);
        dialog.Content = stack;

        bool changeSuccess = false;

        async Task<bool> ExecuteChangeAsync()
        {
            string oldP = oldPwd.Password;
            string newP1 = newPwd1.Password;
            string newP2 = newPwd2.Password;

            var settings = settingsService.LoadSettings();
            bool oldValid = false;
            if (!string.IsNullOrEmpty(settings.PasswordHash) && !string.IsNullOrEmpty(settings.PasswordSalt))
            {
                oldValid = PasswordSecurityService.VerifyPassword(oldP, settings.PasswordHash, settings.PasswordSalt);
            }
            else
            {
                oldValid = StorageService.TryVerifyPassphrase(storageService.DatabasePath, oldP, out _);
            }

            if (!oldValid)
            {
                errText.Text = "目前舊密碼不正確。";
                errText.Visibility = Visibility.Visible;
                oldPwd.SelectAll();
                oldPwd.Focus(FocusState.Programmatic);
                return false;
            }

            if (!PasswordSecurityService.ValidatePassword(newP1, out string validationErr))
            {
                errText.Text = validationErr;
                errText.Visibility = Visibility.Visible;
                newPwd1.Focus(FocusState.Programmatic);
                return false;
            }

            if (newP1 != newP2)
            {
                errText.Text = "兩次輸入的新密碼不相符。";
                errText.Visibility = Visibility.Visible;
                newPwd2.SelectAll();
                newPwd2.Focus(FocusState.Programmatic);
                return false;
            }

            try
            {
                await storageService.ChangePasswordAsync(oldP, newP1);
                var (hash, salt) = PasswordSecurityService.HashPassword(newP1);
                settingsService.UpdateSecuritySettings(true, hash, salt);
                changeSuccess = true;
                return true;
            }
            catch (Exception ex)
            {
                errText.Text = $"修改密碼失敗：{ex.Message}";
                errText.Visibility = Visibility.Visible;
                return false;
            }
        }

        oldPwd.Loaded += (s, e) => oldPwd.Focus(FocusState.Programmatic);

        oldPwd.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                newPwd1.Focus(FocusState.Programmatic);
            }
        };

        newPwd1.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                newPwd2.Focus(FocusState.Programmatic);
            }
        };

        newPwd2.KeyDown += async (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                if (await ExecuteChangeAsync())
                {
                    dialog.Hide();
                }
            }
        };

        dialog.PrimaryButtonClick += async (s, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (!await ExecuteChangeAsync())
                {
                    args.Cancel = true;
                }
            }
            finally
            {
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();

        if (changeSuccess)
        {
            var tipDialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "✅ 主密碼修改成功",
                Content = "資料庫金鑰已成功更新，下次啟動請使用新密碼解鎖。",
                CloseButtonText = "確定"
            };
            await tipDialog.ShowAsync();
        }
    }

    /// <summary>
    /// Dialog for disabling password protection and decrypting database back to standard SQLite.
    /// Supports Enter key submission (RS-ISSUE-0039).
    /// </summary>
    public static async Task ShowDisablePasswordDialogAsync(XamlRoot xamlRoot, IStorageService storageService, IAppSettingsService settingsService)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "⚠️ 關閉密碼保護 (解密資料庫)",
            PrimaryButtonText = "確認解除密碼",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        var stack = new StackPanel { Spacing = 12, Width = 380 };
        var intro = new TextBlock
        {
            Text = "關閉後，資料庫將解密還原為一般 SQLite 檔案。\n日後啟動本軟體將直接開啟，不再需要輸入密碼。\n\n請輸入目前主密碼以確認解除：",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };

        var pwdBox = new PasswordBox { PlaceholderText = "請輸入目前主密碼" };
        var errText = new TextBlock
        {
            Foreground = new SolidColorBrush(Colors.IndianRed),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap
        };

        stack.Children.Add(intro);
        stack.Children.Add(pwdBox);
        stack.Children.Add(errText);
        dialog.Content = stack;

        bool disableSuccess = false;

        async Task<bool> ExecuteDisableAsync()
        {
            string entered = pwdBox.Password;
            var settings = settingsService.LoadSettings();
            bool valid = false;

            if (!string.IsNullOrEmpty(settings.PasswordHash) && !string.IsNullOrEmpty(settings.PasswordSalt))
            {
                valid = PasswordSecurityService.VerifyPassword(entered, settings.PasswordHash, settings.PasswordSalt);
            }
            else
            {
                valid = StorageService.TryVerifyPassphrase(storageService.DatabasePath, entered, out _);
            }

            if (!valid)
            {
                errText.Text = "密碼不正確，無法解除保護。";
                errText.Visibility = Visibility.Visible;
                pwdBox.SelectAll();
                pwdBox.Focus(FocusState.Programmatic);
                return false;
            }

            try
            {
                await storageService.DisableEncryptionAsync(entered);
                settingsService.UpdateSecuritySettings(false, null, null);
                disableSuccess = true;
                return true;
            }
            catch (Exception ex)
            {
                errText.Text = $"解除失敗：{ex.Message}";
                errText.Visibility = Visibility.Visible;
                return false;
            }
        }

        pwdBox.Loaded += (s, e) => pwdBox.Focus(FocusState.Programmatic);

        pwdBox.KeyDown += async (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                if (await ExecuteDisableAsync())
                {
                    dialog.Hide();
                }
            }
        };

        dialog.PrimaryButtonClick += async (s, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (!await ExecuteDisableAsync())
                {
                    args.Cancel = true;
                }
            }
            finally
            {
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();

        if (disableSuccess)
        {
            var tipDialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "✅ 密碼保護已關閉",
                Content = "資料庫已成功解密還原為一般 SQLite 檔案。\n之後開啟軟體將直接進入主畫面。",
                CloseButtonText = "確定"
            };
            await tipDialog.ShowAsync();
        }
    }

    /// <summary>
    /// Prompts the user to enter the master password before restoring an encrypted backup package.
    /// Conforms to RS-ISSUE-0041, AR-ISSUE-0041, DP-ISSUE-0041.
    /// </summary>
    public static async Task<string?> ShowRestorePasswordDialogAsync(XamlRoot xamlRoot, string backupFileName)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "🔐 輸入備份主密碼",
            PrimaryButtonText = "驗證並開始還原",
            CloseButtonText = "取消還原",
            DefaultButton = ContentDialogButton.Primary
        };

        var stack = new StackPanel { Spacing = 12, Width = 420 };
        var introText = new TextBlock
        {
            Text = $"所選備份版本「{backupFileName}」受 SQLCipher 加密保護。\n請輸入原備份時設定的主密碼，以解密並驗證資料庫完整性：",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };

        var pwdBox = new PasswordBox
        {
            PlaceholderText = "請輸入備份主密碼",
            PasswordChar = "●",
            IsPasswordRevealButtonEnabled = true
        };

        var errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Colors.IndianRed),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap
        };

        stack.Children.Add(introText);
        stack.Children.Add(pwdBox);
        stack.Children.Add(errorText);
        dialog.Content = stack;

        string? enteredPassword = null;

        bool ValidateInput()
        {
            string entered = pwdBox.Password;
            if (string.IsNullOrEmpty(entered))
            {
                errorText.Text = "請輸入備份主密碼。";
                errorText.Visibility = Visibility.Visible;
                pwdBox.Focus(FocusState.Programmatic);
                return false;
            }
            enteredPassword = entered;
            return true;
        }

        pwdBox.Loaded += (s, e) => pwdBox.Focus(FocusState.Programmatic);

        pwdBox.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                if (ValidateInput())
                {
                    dialog.Hide();
                }
            }
        };

        dialog.PrimaryButtonClick += (s, args) =>
        {
            if (!ValidateInput())
            {
                args.Cancel = true;
            }
        };

        var result = await dialog.ShowAsync();
        if ((result == ContentDialogResult.Primary || result == ContentDialogResult.None) && !string.IsNullOrEmpty(enteredPassword))
        {
            return enteredPassword;
        }

        return null;
    }
}

