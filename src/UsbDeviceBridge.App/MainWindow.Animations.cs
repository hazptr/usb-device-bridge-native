using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media.Animation;
using UsbDeviceBridge.App.ViewModels;

namespace UsbDeviceBridge.App;

/// <summary>
/// Partial class containing device card and toast animations for <see cref="MainWindow"/>.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, double> _lastDeviceCardTopById = [];
    private bool _pendingDeviceReorderAnimation;

    private void Devices_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add
            or NotifyCollectionChangedAction.Remove
            or NotifyCollectionChangedAction.Move
            or NotifyCollectionChangedAction.Replace
            or NotifyCollectionChangedAction.Reset)
        {
            _pendingDeviceReorderAnimation = true;
            Dispatcher.BeginInvoke(AnimateDeviceReorder, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void AnimateDeviceReorder()
    {
        if (!_pendingDeviceReorderAnimation)
            return;

        _pendingDeviceReorderAnimation = false;

        if (_lastDeviceCardTopById.Count == 0)
        {
            CaptureDeviceCardPositions();
            return;
        }

        var animatedAny = false;

        var deviceItemsControl = DeviceListPanel.DeviceItemsHost;

        foreach (var item in deviceItemsControl.Items)
        {
            if (item is not DeviceViewModel vm || string.IsNullOrEmpty(vm.InstanceId))
                continue;

            if (deviceItemsControl.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement presenter)
                continue;

            var newTop = presenter.TransformToAncestor(deviceItemsControl).Transform(new System.Windows.Point(0, 0)).Y;
            if (!_lastDeviceCardTopById.TryGetValue(vm.InstanceId, out var oldTop))
            {
                var (entryTranslate, scale) = EnsureCardTransforms(presenter);
                presenter.Opacity = 0;
                entryTranslate.Y = 14;
                scale.ScaleX = 0.97;
                scale.ScaleY = 0.97;

                var settleEase = new CubicEase { EasingMode = EasingMode.EaseOut };
                entryTranslate.BeginAnimation(
                    System.Windows.Media.TranslateTransform.YProperty,
                    new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = settleEase });
                presenter.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
                scale.BeginAnimation(
                    System.Windows.Media.ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = settleEase });
                scale.BeginAnimation(
                    System.Windows.Media.ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = settleEase });

                animatedAny = true;
                continue;
            }

            var deltaY = oldTop - newTop;
            if (Math.Abs(deltaY) < 1.0)
                continue;

            var (translate, _) = EnsureCardTransforms(presenter);

            animatedAny = true;
            translate.Y = deltaY;
            var slide = new DoubleAnimation(deltaY, 0, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            translate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
        }

        if (animatedAny)
        {
            Dispatcher.BeginInvoke(CaptureDeviceCardPositions, System.Windows.Threading.DispatcherPriority.Render);
            return;
        }

        CaptureDeviceCardPositions();
    }

    private void CaptureDeviceCardPositions()
    {
        _lastDeviceCardTopById.Clear();

        var deviceItemsControl = DeviceListPanel.DeviceItemsHost;

        foreach (var item in deviceItemsControl.Items)
        {
            if (item is not DeviceViewModel vm || string.IsNullOrEmpty(vm.InstanceId))
                continue;

            if (deviceItemsControl.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement presenter)
                continue;

            var top = presenter.TransformToAncestor(deviceItemsControl).Transform(new System.Windows.Point(0, 0)).Y;
            _lastDeviceCardTopById[vm.InstanceId] = top;
        }
    }

    private static (System.Windows.Media.TranslateTransform Translate, System.Windows.Media.ScaleTransform Scale) EnsureCardTransforms(FrameworkElement presenter)
    {
        presenter.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);

        if (presenter.RenderTransform is not System.Windows.Media.TransformGroup group)
        {
            var existing = presenter.RenderTransform;
            group = new System.Windows.Media.TransformGroup();
            if (existing is not null && existing != System.Windows.Media.Transform.Identity)
                group.Children.Add(existing);

            presenter.RenderTransform = group;
        }

        var scale = group.Children.OfType<System.Windows.Media.ScaleTransform>().FirstOrDefault();
        if (scale is null)
        {
            scale = new System.Windows.Media.ScaleTransform(1, 1);
            group.Children.Insert(0, scale);
        }

        var translate = group.Children.OfType<System.Windows.Media.TranslateTransform>().FirstOrDefault();
        if (translate is null)
        {
            translate = new System.Windows.Media.TranslateTransform();
            group.Children.Add(translate);
        }

        return (translate, scale);
    }
}
