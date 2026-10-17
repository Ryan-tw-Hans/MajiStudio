using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MajiOCR_Windows.Models;

namespace MajiOCR_Windows.Services.Storage;

public interface ITagStorageService
{
    Task<PagedResult<TagDefinition>> GetTagsAsync(string? search, int page = 0, CancellationToken ct = default);
    Task<IReadOnlyList<TagDefinition>> FindTagsAsync(IReadOnlyList<string> ids, CancellationToken ct = default);
    Task<TagDefinition?> FindTagAsync(string tagId, CancellationToken ct = default);
    Task<TagState?> GetTagStateAsync(string contactId, CancellationToken ct = default);
    Task<TagMutationResult> SetTagsAsync(string contactId, int expectedRevision, IReadOnlyList<TagChoice> choices, CancellationToken ct = default);
    Task<TagMutationResult> CreateTagAsync(string name, CancellationToken ct = default);
    Task<TagMutationResult> RenameTagAsync(TagDefinition tag, string name, CancellationToken ct = default);
    Task<TagDeletePreview> PreviewTagDeleteAsync(string tagId, CancellationToken ct = default);
    Task<TagMutationResult> DeleteTagAsync(TagDeletePreview preview, CancellationToken ct = default);
    Task<PagedResult<ContactSummaryItem>> QueryContactsAsync(ContactQuery query, int page = 0, int pageSize = 30, CancellationToken ct = default);
}
