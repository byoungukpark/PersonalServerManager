using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using OxyPlot;

namespace PersonalServerManager.Views;

public partial class GroupMonitoringView : UserControl
{
    public GroupMonitoringView()
    {
        InitializeComponent();
    }

    // 그래프 위에서도 마우스 휠은 상위 목록을 스크롤한다.
    private void PlotView_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        for (var parent = VisualTreeHelper.GetParent((System.Windows.DependencyObject)sender);
             parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollViewer scrollViewer) continue;
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta / 3.0);
            e.Handled = true;
            break;
        }
    }
}

/// <summary>각 PlotView에 독립적인 그래프 모델을 연결해 화면 재진입 시 모델 공유를 막는다.</summary>
public sealed class PlotModelFactoryConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Func<PlotModel> createPlot ? createPlot() : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
