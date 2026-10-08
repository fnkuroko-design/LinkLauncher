using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LinkLauncher.Models;
using LinkLauncher.Services;

namespace LinkLauncher;

public partial class MainWindow
{
    private const string CategoryOrderDataFormat = "LinkLauncher.CategoryOrder.v1";
    private const string LinkOrderDataFormat = "LinkLauncher.LinkOrder.v1";

    private string? _orderCategorySourceId;
    private Point _orderCategoryPressStart;
    private string? _orderDropCategoryId;
    private bool _orderDropCategoryAfter;
    private Border? _orderDropCategoryBorder;
    private string? _orderDropLinkId;
    private bool _orderDropLinkAfter;
    private Border? _orderDropLinkBorder;

    public bool IsOrderDragInProgress { get; private set; }

    private void CategoryOrder_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _orderCategorySourceId = null;
        if (e.ChangedButton != MouseButton.Left ||
            FindAncestor<ToggleButton>(e.OriginalSource as DependencyObject) != null ||
            FindAncestor<Button>(e.OriginalSource as DependencyObject) != null)
            return;

        var item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not CategoryNode node) return;

        _orderCategorySourceId = node.Id;
        _orderCategoryPressStart = e.GetPosition(CategoryTree);
    }

    private void CategoryOrder_MouseMove(object sender, MouseEventArgs e)
    {
        string? sourceId = _orderCategorySourceId;
        if (sourceId == null || e.LeftButton != MouseButtonState.Pressed) return;

        Point point = e.GetPosition(CategoryTree);
        if (Math.Abs(point.X - _orderCategoryPressStart.X) <= SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _orderCategoryPressStart.Y) <= SystemParameters.MinimumVerticalDragDistance)
            return;

        if (!_library.Categories.Any(category => category.Id == sourceId))
        {
            _orderCategorySourceId = null;
            return;
        }

        _orderCategorySourceId = null;
        var data = new DataObject();
        data.SetData(CategoryOrderDataFormat, sourceId, false);
        IsOrderDragInProgress = true;
        try
        {
            DragDrop.DoDragDrop(CategoryTree, data, DragDropEffects.Move);
        }
        finally
        {
            IsOrderDragInProgress = false;
            ClearCategoryDropIndicator();
            _orderCategorySourceId = null;
            RequestAutoDismiss();
        }

        e.Handled = true;
    }

    private void CategoryOrder_MouseUp(object sender, MouseButtonEventArgs e) => _orderCategorySourceId = null;

    private void CategoryOrder_DragOver(object sender, DragEventArgs e)
    {
        if (!HasOrderData(e.Data)) return;

        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (!e.Data.GetDataPresent(CategoryOrderDataFormat, false))
        {
            ClearCategoryDropIndicator();
            return;
        }
        if (e.Data.GetData(CategoryOrderDataFormat, false) is not string sourceId ||
            _library.Categories.FirstOrDefault(category => category.Id == sourceId) is not Category source ||
            FindAncestor<Button>(e.OriginalSource as DependencyObject) != null)
        {
            ClearCategoryDropIndicator();
            return;
        }

        var targetContainer = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (targetContainer?.DataContext is not CategoryNode targetNode)
        {
            ClearCategoryDropIndicator();
            return;
        }
        var target = _library.Categories.FirstOrDefault(category => category.Id == targetNode.Id);
        if (target == null || target.Id == source.Id || target.ParentId != source.ParentId)
        {
            ClearCategoryDropIndicator();
            return;
        }

        if (targetContainer.Template.FindName("HeaderFrame", targetContainer) is not Border header)
        {
            ClearCategoryDropIndicator();
            return;
        }
        bool after = e.GetPosition(header).Y >= header.ActualHeight / 2;
        SetCategoryDropIndicator(target.Id, after, targetContainer);
        e.Effects = DragDropEffects.Move;
    }

    private void CategoryOrder_DragLeave(object sender, DragEventArgs e)
    {
        if (HasOrderData(e.Data)) ClearCategoryDropIndicator();
    }

    private void CategoryOrder_Drop(object sender, DragEventArgs e)
    {
        if (!HasOrderData(e.Data)) return;

        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (!e.Data.GetDataPresent(CategoryOrderDataFormat, false))
        {
            ClearCategoryDropIndicator();
            return;
        }
        try
        {
            if (e.Data.GetData(CategoryOrderDataFormat, false) is not string sourceId ||
                _library.Categories.FirstOrDefault(category => category.Id == sourceId) is not Category source ||
                FindAncestor<Button>(e.OriginalSource as DependencyObject) != null)
                return;

            var targetContainer = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
            if (targetContainer?.DataContext is not CategoryNode targetNode) return;
            var target = _library.Categories.FirstOrDefault(category => category.Id == targetNode.Id);
            if (target == null || target.Id == source.Id || target.ParentId != source.ParentId) return;

            var siblings = LibraryOrder.GetCategories(_library, source.ParentId).ToList();
            int sourceIndex = siblings.FindIndex(category => category.Id == source.Id);
            int targetIndex = siblings.FindIndex(category => category.Id == target.Id);
            if (sourceIndex < 0 || targetIndex < 0) return;

            if (targetContainer.Template.FindName("HeaderFrame", targetContainer) is not Border header) return;
            bool after = e.GetPosition(header).Y >= header.ActualHeight / 2;
            int insertionIndex = targetIndex + (after ? 1 : 0);
            if (sourceIndex < insertionIndex) insertionIndex--;
            if (insertionIndex == sourceIndex) return;

            if (ApplyChange(library => LibraryOrder.MoveCategory(library, sourceId, insertionIndex)))
                ShowNotice("カテゴリの並び順を保存しました。");
        }
        finally
        {
            ClearCategoryDropIndicator();
        }
    }

    private void LinkOrder_MouseMove(object sender, MouseEventArgs e)
    {
        string? sourceId = _pressedLinkId;
        if (sourceId == null || _view == "recent" || e.LeftButton != MouseButtonState.Pressed) return;

        Point point = e.GetPosition(LinkList);
        if (Math.Abs(point.X - _linkPressStart.X) <= SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _linkPressStart.Y) <= SystemParameters.MinimumVerticalDragDistance)
            return;

        if (!LinkList.Items.OfType<LinkRow>().Any(row => row.Item.Id == sourceId))
        {
            _pressedLinkId = null;
            return;
        }

        // LinkList_MouseUp uses this press state to launch clicked links.
        _pressedLinkId = null;
        var data = new DataObject();
        data.SetData(LinkOrderDataFormat, sourceId, false);
        IsOrderDragInProgress = true;
        try
        {
            DragDrop.DoDragDrop(LinkList, data, DragDropEffects.Move);
        }
        finally
        {
            IsOrderDragInProgress = false;
            ClearLinkDropIndicator();
            _pressedLinkId = null;
            RequestAutoDismiss();
        }

        e.Handled = true;
    }

    private void LinkOrder_DragOver(object sender, DragEventArgs e)
    {
        if (!HasOrderData(e.Data)) return;

        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (!e.Data.GetDataPresent(LinkOrderDataFormat, false))
        {
            ClearLinkDropIndicator();
            return;
        }
        if (_view == "recent" || e.Data.GetData(LinkOrderDataFormat, false) is not string sourceId ||
            FindAncestor<Button>(e.OriginalSource as DependencyObject) != null)
        {
            ClearLinkDropIndicator();
            return;
        }

        var targetContainer = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (targetContainer?.DataContext is not LinkRow targetRow || targetRow.Item.Id == sourceId)
        {
            ClearLinkDropIndicator();
            return;
        }

        bool after = e.GetPosition(targetContainer).Y >= targetContainer.ActualHeight / 2;
        SetLinkDropIndicator(targetRow.Item.Id, after, targetContainer);
        e.Effects = DragDropEffects.Move;
    }

    private void LinkOrder_DragLeave(object sender, DragEventArgs e)
    {
        if (HasOrderData(e.Data)) ClearLinkDropIndicator();
    }

    private void LinkOrder_Drop(object sender, DragEventArgs e)
    {
        if (!HasOrderData(e.Data)) return;

        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (!e.Data.GetDataPresent(LinkOrderDataFormat, false))
        {
            ClearLinkDropIndicator();
            return;
        }
        try
        {
            if (_view == "recent" || e.Data.GetData(LinkOrderDataFormat, false) is not string sourceId ||
                FindAncestor<Button>(e.OriginalSource as DependencyObject) != null)
                return;

            var targetContainer = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (targetContainer?.DataContext is not LinkRow targetRow || targetRow.Item.Id == sourceId) return;

            List<string> displayedIds = LinkList.Items.OfType<LinkRow>().Select(row => row.Item.Id).ToList();
            int sourceIndex = displayedIds.IndexOf(sourceId);
            int targetIndex = displayedIds.IndexOf(targetRow.Item.Id);
            if (sourceIndex < 0 || targetIndex < 0) return;

            bool after = e.GetPosition(targetContainer).Y >= targetContainer.ActualHeight / 2;
            int insertionIndex = targetIndex + (after ? 1 : 0);
            if (sourceIndex < insertionIndex) insertionIndex--;
            if (insertionIndex == sourceIndex) return;

            if (ApplyChange(library => LibraryOrder.MoveVisibleLinks(library, displayedIds, sourceId, insertionIndex), false))
                ShowNotice("リンクの並び順を保存しました。");
        }
        finally
        {
            ClearLinkDropIndicator();
        }
    }

    private static bool HasOrderData(IDataObject data) =>
        data.GetDataPresent(CategoryOrderDataFormat, false) || data.GetDataPresent(LinkOrderDataFormat, false);

    private void SetLinkDropIndicator(string? targetId, bool after, ListBoxItem? container)
    {
        if (targetId == null || container == null)
        {
            ClearLinkDropIndicator();
            return;
        }
        if (_orderDropLinkId == targetId && _orderDropLinkAfter == after) return;
        ClearLinkDropIndicator();
        if (container.Template.FindName("Row", container) is not Border border) return;

        _orderDropLinkId = targetId;
        _orderDropLinkAfter = after;
        _orderDropLinkBorder = border;
        border.BorderThickness = after ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
    }

    private void SetCategoryDropIndicator(string? targetId, bool after, TreeViewItem? container)
    {
        if (targetId == null || container == null)
        {
            ClearCategoryDropIndicator();
            return;
        }
        if (_orderDropCategoryId == targetId && _orderDropCategoryAfter == after) return;
        ClearCategoryDropIndicator();
        if (container.Template.FindName("HeaderFrame", container) is not Border border) return;

        _orderDropCategoryId = targetId;
        _orderDropCategoryAfter = after;
        _orderDropCategoryBorder = border;
        border.BorderThickness = after ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
    }

    private void ClearLinkDropIndicator()
    {
        if (_orderDropLinkBorder != null) _orderDropLinkBorder.BorderThickness = new Thickness(0);
        _orderDropLinkId = null;
        _orderDropLinkBorder = null;
        _orderDropLinkAfter = false;
    }

    private void ClearCategoryDropIndicator()
    {
        if (_orderDropCategoryBorder != null) _orderDropCategoryBorder.BorderThickness = new Thickness(0);
        _orderDropCategoryId = null;
        _orderDropCategoryBorder = null;
        _orderDropCategoryAfter = false;
    }

    private bool CanMoveCategoryOneStep(string categoryId, int delta)
    {
        if (delta is not (-1 or 1)) return false;
        var category = _library.Categories.FirstOrDefault(item => item.Id == categoryId);
        if (category == null) return false;
        var siblings = LibraryOrder.GetCategories(_library, category.ParentId).ToList();
        int index = siblings.FindIndex(item => item.Id == categoryId);
        return index >= 0 && index + delta >= 0 && index + delta < siblings.Count;
    }

    private void MoveCategoryOneStep(string categoryId, int delta)
    {
        if (!CanMoveCategoryOneStep(categoryId, delta)) return;
        var category = _library.Categories.First(item => item.Id == categoryId);
        var siblings = LibraryOrder.GetCategories(_library, category.ParentId).ToList();
        int index = siblings.FindIndex(item => item.Id == categoryId);
        if (ApplyChange(library => LibraryOrder.MoveCategory(library, categoryId, index + delta)))
            ShowNotice("カテゴリの並び順を保存しました。");
    }

    private bool CanMoveLinkOneStep(string linkId, int delta)
    {
        if (_view == "recent" || delta is not (-1 or 1)) return false;
        var displayedIds = LinkList.Items.OfType<LinkRow>().Select(row => row.Item.Id).ToList();
        int index = displayedIds.IndexOf(linkId);
        return index >= 0 && index + delta >= 0 && index + delta < displayedIds.Count;
    }

    private void MoveLinkOneStep(string linkId, int delta)
    {
        if (!CanMoveLinkOneStep(linkId, delta)) return;
        var displayedIds = LinkList.Items.OfType<LinkRow>().Select(row => row.Item.Id).ToList();
        int index = displayedIds.IndexOf(linkId);
        if (ApplyChange(library => LibraryOrder.MoveVisibleLinks(library, displayedIds, linkId, index + delta), false))
            ShowNotice("リンクの並び順を保存しました。");
    }
}
