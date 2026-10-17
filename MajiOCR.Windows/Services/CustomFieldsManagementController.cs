using System;
using System.Collections.Generic;
using MajiOCR_Windows.Models;

namespace MajiOCR_Windows.Services;

/// <summary>
/// Controller mediating custom field management UI interactions and persistence.
/// Provides safe exception handling, validation, and testable UI actions for rename and activation toggles.
/// </summary>
public class CustomFieldsManagementController
{
    private readonly CustomFieldCatalogService _catalogService;

    public CustomFieldsManagementController(CustomFieldCatalogService catalogService)
    {
        _catalogService = catalogService;
    }

    public CustomFieldCatalogService CatalogService => _catalogService;

    /// <summary>
    /// Safely attempts to load all custom field definitions.
    /// Returns false with error message if file is corrupted or unknown new version.
    /// </summary>
    public bool TryLoadAllDefinitions(out List<CustomFieldDefinition> definitions, out string? errorMessage)
    {
        try
        {
            definitions = _catalogService.GetAllDefinitions();
            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            definitions = new List<CustomFieldDefinition>();
            errorMessage = $"無法載入自訂名稱設定: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Safely attempts to add a new custom field definition.
    /// </summary>
    public bool TryAddDefinition(string rawName, out CustomFieldDefinition? newDef, out string? errorMessage)
    {
        try
        {
            newDef = _catalogService.AddDefinition(rawName);
            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            newDef = null;
            errorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Safely attempts to rename an existing definition while keeping its ID intact.
    /// </summary>
    public bool TryRenameDefinition(string id, string newRawName, out string? errorMessage)
    {
        try
        {
            _catalogService.RenameDefinition(id, newRawName);
            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Safely attempts to toggle the active state of an existing definition.
    /// </summary>
    public bool TrySetDefinitionActive(string id, bool active, out string? errorMessage)
    {
        try
        {
            _catalogService.SetDefinitionActive(id, active);
            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Executes a toggle of definition active state with recursion suppression and state revert protection.
    /// In case of error, calls revertAction without triggering recursive write loops, and reports error exactly once.
    /// </summary>
    public bool ExecuteActiveToggle(
        string id,
        bool requestedActive,
        Action<bool> revertViewCheckbox,
        Action<string> showErrorMessage)
    {
        if (TrySetDefinitionActive(id, requestedActive, out var err))
        {
            return true;
        }

        // Revert without recursion
        revertViewCheckbox(!requestedActive);
        showErrorMessage(err ?? "無法更新啟用狀態。");
        return false;
    }
}
