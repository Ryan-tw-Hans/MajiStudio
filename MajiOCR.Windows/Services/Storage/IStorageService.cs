using System;
using System.Threading;
using System.Threading.Tasks;
using MajiOCR_Windows.Models;

namespace MajiOCR_Windows.Services.Storage;

public interface IStorageService : IDisposable
{
    bool IsAvailable { get; }
    string? ErrorMessage { get; }
    string StorageRoot { get; }
    string DatabasePath { get; }
    bool IsEncrypted { get; }
    string? CurrentPassphrase { get; }

    void SetCurrentPassphrase(string? passphrase);

    Task<SaveResult> SaveDraftAsync(ContactDraftSnapshot snapshot, CancellationToken ct = default);
    Task<DeleteResult> DeleteContactAsync(string contactId, CancellationToken ct = default);
    Task<PagedResult<ContactSummaryItem>> GetContactsPagedAsync(string? searchQuery, int pageIndex, int pageSize = 30, CancellationToken ct = default);
    Task<ContactDetailModel?> GetContactDetailAsync(string contactId, CancellationToken ct = default);
    string? GetCardImageAbsolutePath(string contactId);
    Task RecoverPendingOperationsAsync(CancellationToken ct = default);

    Task<bool> EnableEncryptionAsync(string newPassword, CancellationToken ct = default);
    Task<bool> ChangePasswordAsync(string oldPassword, string newPassword, CancellationToken ct = default);
    Task<bool> DisableEncryptionAsync(string currentPassword, CancellationToken ct = default);

    void SetFailureInjectionHook(Action<string>? hook);
}
