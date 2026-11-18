using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace NopeView.App;

/// <summary>
/// A settings navigation item. It keeps the visual shape of a NavigationView
/// item while exposing the selection semantics that screen readers expect.
/// </summary>
public sealed partial class SettingsNavItem : Button
{
    public bool IsSelected { get; set; }

    public Action<SettingsNavItem>? SelectionRequested { get; set; }

    internal void RequestSelection() => SelectionRequested?.Invoke(this);

    internal void RaiseSelectedChanged(bool previous)
    {
        if (FrameworkElementAutomationPeer.FromElement(this) is SettingsNavItemAutomationPeer peer)
        {
            peer.RaiseIsSelectedChanged(previous, IsSelected);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer()
        => new SettingsNavItemAutomationPeer(this);
}

public sealed class SettingsNavItemAutomationPeer : ButtonAutomationPeer, ISelectionItemProvider
{
    private readonly SettingsNavItem _owner;

    public SettingsNavItemAutomationPeer(SettingsNavItem owner) : base(owner)
    {
        _owner = owner;
    }

    public bool IsSelected => _owner.IsSelected;

    public IRawElementProviderSimple SelectionContainer => null!;

    public void Select() => _owner.RequestSelection();

    public void AddToSelection() => Select();

    public void RemoveFromSelection()
    {
    }

    protected override object GetPatternCore(PatternInterface patternInterface)
        => patternInterface == PatternInterface.SelectionItem
            ? this
            : base.GetPatternCore(patternInterface);

    internal void RaiseIsSelectedChanged(bool oldValue, bool newValue)
    {
        RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, oldValue, newValue);
    }

    protected override AutomationControlType GetAutomationControlTypeCore()
        => AutomationControlType.ListItem;
}
