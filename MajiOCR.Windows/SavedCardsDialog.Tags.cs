using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows;
public partial class SavedCardsDialog
{
    private IReadOnlyList<string> _filterTags=Array.Empty<string>();
    private bool _matchAll,_untagged,_tagBusy;
    private TextBlock _filterSummary=null!,_detailTags=null!;
    private Button _editTagsButton=null!;
    private StackPanel? _tagWorkspace;
    private TagCatalogPanel? _catalogPanel;
    private ScrollViewer? _tagWorkspaceHost;
    private TagPickerPanel? _editingPicker;
    private string? _initialTagFingerprint;
    private Func<Task>? _saveEditedTags;
    private StackPanel? _leaveTagPrompt;
    private bool _closeDialogAfterTagExit,_ignoreTagClose;
    private string? _selectAfterTagSave;
    private FrameworkElement BuildTagFilters()
    {
        var section=new StackPanel{Spacing=6};
        _filterSummary=new TextBlock{Text="所有分類",TextWrapping=TextWrapping.Wrap};
        section.Children.Add(_filterSummary);
        var filter=new Button{Content="標籤篩選"};filter.Click+=(_,_)=>EditFilters();
        var manager=new Button{Content="管理分類標籤"};manager.Click+=(_,_)=>OpenCatalog();
        section.Children.Add(filter);section.Children.Add(manager);
        return section;
    }
    private void UpdateFilterSummary()=>_filterSummary.Text=_untagged?"未分類":_filterTags.Count==0?"所有分類":$"符合{(_matchAll?"全部":"任一")}標籤（{_filterTags.Count}項）";
    private StackPanel OpenTagWorkspace(string title)
    {
        Interlocked.Increment(ref _pageQueryGeneration);Interlocked.Increment(ref _detailQueryGeneration);
        _leftPane.Visibility=Visibility.Collapsed;_rightPane.Visibility=Visibility.Collapsed;
        var panel=new StackPanel{Spacing=10};
        panel.Children.Add(new TextBlock{Text=title,FontSize=18});
        _tagWorkspaceHost=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxHeight=Math.Max(240,_rootGrid.ActualHeight)};
        Grid.SetColumnSpan(_tagWorkspaceHost,2);Grid.SetRowSpan(_tagWorkspaceHost,2);_rootGrid.Children.Add(_tagWorkspaceHost);_tagWorkspace=panel;
        return panel;
    }
    private async Task CloseTagWorkspaceAsync()
    {
        if(_tagBusy||_catalogPanel?.Busy==true)return;
        if(_tagWorkspaceHost is not null)_rootGrid.Children.Remove(_tagWorkspaceHost);
        _tagWorkspaceHost=null;
        _tagWorkspace=null;_catalogPanel=null;_editingPicker=null;_initialTagFingerprint=null;_saveEditedTags=null;_leaveTagPrompt=null;
        _leftPane.Visibility=Visibility.Visible;_rightPane.Visibility=Visibility.Visible;
        long expectedGeneration=Volatile.Read(ref _pageQueryGeneration)+1;
        await LoadPageAsync(_currentPage);
        if(_closed || expectedGeneration!=Volatile.Read(ref _pageQueryGeneration) || _tagWorkspace is not null){_selectAfterTagSave=null;return;}
        if(_selectAfterTagSave is string contact){
            _selectAfterTagSave=null;
            var item=_contactsListView.Items.OfType<ContactSummaryItem>().FirstOrDefault(i=>i.ContactId==contact);
            if(item is not null)_contactsListView.SelectedItem=item;
            else{_emptyNoticeText.Text="標籤已更新；目前這一頁未顯示該名片。";_emptyNoticeText.Visibility=Visibility.Visible;}
        }
    }
    private bool HasEditedTags => _editingPicker is not null && TagChoice.Fingerprint(_editingPicker.Choices)!=_initialTagFingerprint;
    private bool HandleUnsavedTagClose()
    {
        if(_ignoreTagClose || !HasEditedTags)return false;
        ShowTagLeavePrompt(true);return true;
    }
    private void ShowTagLeavePrompt(bool closeDialog)
    {
        if(_tagWorkspace is null || _leaveTagPrompt is not null)return;
        _closeDialogAfterTagExit=closeDialog;
        _leaveTagPrompt=new StackPanel{Spacing=6};
        _leaveTagPrompt.Children.Add(new TextBlock{Text="標籤選擇尚未保存，請選擇離開方式。",TextWrapping=TextWrapping.Wrap});
        var save=new Button{Content="儲存標籤後離開"};
        var discard=new Button{Content="捨棄變更"};
        var stay=new Button{Content="繼續編輯"};
        save.Click+=async(_,_)=>{if(_saveEditedTags is not null&&!_tagBusy)await _saveEditedTags();};
        discard.Click+=async(_,_)=>{
            if(_tagBusy)return;bool close=_closeDialogAfterTagExit;
            await CloseTagWorkspaceAsync();
            if(close){_ignoreTagClose=true;Hide();}
        };
        stay.Click+=(_,_)=>{if(_tagBusy)return;_tagWorkspace.Children.Remove(_leaveTagPrompt);_leaveTagPrompt=null;_closeDialogAfterTagExit=false;};
        _leaveTagPrompt.Children.Add(save);_leaveTagPrompt.Children.Add(discard);_leaveTagPrompt.Children.Add(stay);
        _tagWorkspace.Children.Add(_leaveTagPrompt);discard.Focus(FocusState.Programmatic);
        _tagWorkspaceHost?.ChangeView(null,double.MaxValue,null);
    }
    private async Task EditContactTagsAsync()
    {
        var target=_currentDetail;
        if(target is null||_storageService is not ITagStorageService tags||_tagWorkspace is not null)return;
        var panel=OpenTagWorkspace("調整名片標籤："+target.DisplayTitle);
        var picker=new TagPickerPanel(tags,target.Tags.Select(t=>new TagChoice(t.TagId,t.DisplayName)));
        picker.MaxHeight=380;panel.Children.Add(picker);
        _editingPicker=picker;_initialTagFingerprint=TagChoice.Fingerprint(picker.Choices);
        var notice=new TextBlock{TextWrapping=TextWrapping.Wrap};panel.Children.Add(notice);
        var save=new Button{Content="儲存標籤"};var cancel=new Button{Content="取消"};
        panel.Children.Add(save);panel.Children.Add(cancel);
        cancel.Click+=async(_,_)=>{if(HasEditedTags)ShowTagLeavePrompt(false);else await CloseTagWorkspaceAsync();};
        _saveEditedTags=async()=>{
            if(_tagBusy)return;_tagBusy=true;picker.IsEnabled=false;save.IsEnabled=false;cancel.IsEnabled=false;
            try{
                var result=await tags.SetTagsAsync(target.ContactId,target.TagRevision,picker.Choices);
                if(_closed)return;
                if(result.Succeeded){bool close=_closeDialogAfterTagExit;_tagBusy=false;_selectAfterTagSave=target.ContactId;await CloseTagWorkspaceAsync();if(close){_ignoreTagClose=true;Hide();}}
                else notice.Text=result.Error+" 可取消後重新開啟此名片。";
            }catch{if(!_closed)notice.Text="無法保存，選擇仍保留，請稍後再試。";}
            finally{_tagBusy=false;picker.IsEnabled=true;save.IsEnabled=true;cancel.IsEnabled=true;}
        };
        save.Click+=async(_,_)=>{if(_saveEditedTags is not null)await _saveEditedTags();};
        await Task.CompletedTask;
    }
    private async void EditFilters()
    {
        if(_storageService is not ITagStorageService tags||_tagWorkspace is not null)return;
        var definitions=await tags.FindTagsAsync(_filterTags);
        var selected=definitions.Select(t=>new TagChoice(t.TagId,t.DisplayName)).ToArray();
        if(_tagWorkspace is not null||_closed)return;
        var panel=OpenTagWorkspace("標籤篩選");
        var picker=new TagPickerPanel(tags,selected,false);picker.MaxHeight=380;panel.Children.Add(picker);
        var all=new CheckBox{Content="符合全部選取標籤（取消勾選為任一）",IsChecked=_matchAll};
        var untagged=new CheckBox{Content="只查未分類名片",IsChecked=_untagged};
        picker.IsEnabled=!_untagged;
        untagged.Checked+=(_,_)=>picker.IsEnabled=false;untagged.Unchecked+=(_,_)=>picker.IsEnabled=true;
        panel.Children.Add(all);panel.Children.Add(untagged);
        var apply=new Button{Content="套用篩選"};var cancel=new Button{Content="取消"};
        panel.Children.Add(apply);panel.Children.Add(cancel);
        cancel.Click+=async(_,_)=>await CloseTagWorkspaceAsync();
        apply.Click+=async(_,_)=>{
            _untagged=untagged.IsChecked==true;_matchAll=all.IsChecked==true;
            _filterTags=_untagged?Array.Empty<string>():picker.Choices.Where(t=>t.TagId is not null).Select(t=>t.TagId!).ToArray();
            UpdateFilterSummary();_currentPage=0;await CloseTagWorkspaceAsync();
        };
    }
    private void OpenCatalog()
    {
        if(_storageService is not ITagStorageService tags||_tagWorkspace is not null)return;
        var panel=OpenTagWorkspace("管理分類標籤");
        _catalogPanel=new TagCatalogPanel(tags){MaxHeight=480};panel.Children.Add(_catalogPanel);
        var back=new Button{Content="返回已存名片"};back.Click+=async(_,_)=>await CloseTagWorkspaceAsync();panel.Children.Add(back);
    }
}
