using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows.ViewModels;
public partial class MainViewModel
{
    private IReadOnlyList<TagChoice> _classification = Array.Empty<TagChoice>();
    private string _savedTagFingerprint = TagChoice.Fingerprint(Array.Empty<TagChoice>());
    private int _currentTagRevision;
    private long _classificationGeneration;
    public IReadOnlyList<TagChoice> Classification => _classification;
    public int CurrentTagRevision => _currentTagRevision;
    public bool HasUnsavedTags => TagChoice.Fingerprint(_classification) != _savedTagFingerprint;
    public string ClassificationSummary => _classification.Count == 0 ? "未分類" : string.Join("、",_classification.Select(t=>t.DisplayName));
    public void SetClassification(IEnumerable<TagChoice> choices)
    {
        _classification = choices
            .GroupBy(c => TagNames.Key(c.DisplayName), StringComparer.Ordinal)
            .Select(g => g.FirstOrDefault(c => c.TagId is not null) ?? g.First())
            .ToArray();
        NotifyClassification();
    }
    private void NotifyClassification()
    {
        _classificationGeneration++;
        OnPropertyChanged(nameof(ClassificationSummary));
        OnPropertyChanged(nameof(HasUnsavedTags));
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }
    public void LoadClassificationFromDetail(IReadOnlyList<TagDefinition> tags, int tagRevision)
    {
        _classification = tags.Select(t => new TagChoice(t.TagId, t.DisplayName)).ToArray();
        _savedTagFingerprint = TagChoice.Fingerprint(_classification);
        _currentTagRevision = tagRevision;
        NotifyClassification();
    }
    private void ResetClassification()
    {
        _classification=Array.Empty<TagChoice>();
        _savedTagFingerprint=TagChoice.Fingerprint(_classification);
        _currentTagRevision=0; NotifyClassification();
    }
    private void AcceptSavedTags(TagState? state,IReadOnlyList<TagChoice> submitted)
    {
        if(state is null) return;
        var resolved=state.Tags.Select(t=>new TagChoice(t.TagId,t.DisplayName)).ToArray();
        if(TagChoice.Fingerprint(_classification)==TagChoice.Fingerprint(submitted))
            _classification=resolved;
        else if(state.ResolvedTokens is not null)
        {
            var byId=state.Tags.ToDictionary(t=>t.TagId);
            _classification=_classification.Select(c=>{
                if(c.TagId is null && state.ResolvedTokens.TryGetValue(c.Token,out var id) && byId.TryGetValue(id,out var definition))
                    return new TagChoice(id,definition.DisplayName);
                return c;
            }).ToArray();
        }
        _savedTagFingerprint=TagChoice.Fingerprint(resolved);
        _currentTagRevision=state.Revision;
        NotifyClassification();
    }
    public async Task ReloadClassificationAsync()
    {
        if(_storageService is not ITagStorageService tags)return;
        string contact=_currentContactId;long generation=_classificationGeneration;
        var state=await tags.GetTagStateAsync(contact);
        if(contact!=_currentContactId || generation!=_classificationGeneration || state is null)return;
        _classification=state.Tags.Select(t=>new TagChoice(t.TagId,t.DisplayName)).ToArray();
        _savedTagFingerprint=TagChoice.Fingerprint(_classification);
        _currentTagRevision=state.Revision;NotifyClassification();
    }
    public async Task RefreshClassificationAsync()
    {
        if(_storageService is not ITagStorageService tags) return;
        string contact=_currentContactId;long generation=_classificationGeneration;bool wasDirty=HasUnsavedTags;
        var state=await tags.GetTagStateAsync(contact);
        if(contact!=_currentContactId || generation!=_classificationGeneration) return;
        if(state is not null)
        {
            if(!wasDirty && !HasUnsavedTags)
            {
                _classification=state.Tags.Select(t=>new TagChoice(t.TagId,t.DisplayName)).ToArray();
                _savedTagFingerprint=TagChoice.Fingerprint(_classification);
                _currentTagRevision=state.Revision;
            }
            else if(state.Revision!=_currentTagRevision)
                StatusMessage="這張名片的分類已變動；保留未存選擇，請取消標籤編輯並重新載入後再調整。";
        }
        // Resolve names of selected stable IDs, including unsaved selections.
        var definitions=await tags.FindTagsAsync(_classification.Where(c=>c.TagId is not null).Select(c=>c.TagId!).ToArray());
        if(contact!=_currentContactId || generation!=_classificationGeneration)return;
        var byId=definitions.ToDictionary(t=>t.TagId);
        _classification=_classification.Select(choice=>{
            if(choice.TagId is null)return choice;
            if(byId.TryGetValue(choice.TagId,out var definition))return new TagChoice(choice.TagId,definition.DisplayName);
            StatusMessage="選取的標籤已刪除，請移除後重新選取；草稿仍保留。";return choice;
        }).ToArray();
        NotifyClassification();
    }
}
