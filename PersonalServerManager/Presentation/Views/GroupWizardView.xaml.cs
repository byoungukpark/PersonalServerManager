using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PersonalServerManager.Presentation.ViewModels;

namespace PersonalServerManager.Presentation.Views;

public partial class GroupWizardView : UserControl
{
    private Point _dragStart;
    private StartOrderChoice? _dragSource;

    public GroupWizardView() => InitializeComponent();

    private void OrderList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(OrderList);
        _dragSource = FindListItem(e.OriginalSource as DependencyObject)?.DataContext as StartOrderChoice;
    }

    private void OrderList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSource is null) return;
        var current = e.GetPosition(OrderList);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(OrderList, _dragSource, DragDropEffects.Move);
        _dragSource = null;
    }

    private void OrderList_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not GroupWizardViewModel model ||
            e.Data.GetData(typeof(StartOrderChoice)) is not StartOrderChoice source ||
            FindListItem(e.OriginalSource as DependencyObject)?.DataContext is not StartOrderChoice target) return;
        model.MoveOrder(source, target);
    }

    private static ListBoxItem? FindListItem(DependencyObject? element)
    {
        while (element is not null && element is not ListBoxItem)
            element = VisualTreeHelper.GetParent(element);
        return element as ListBoxItem;
    }
}
