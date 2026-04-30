using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UserControl = System.Windows.Controls.UserControl;

namespace UsbDeviceBridge.App.Views.Main;

public partial class ToastOverlayControl : UserControl
{
    /// <summary>
    /// Raised when the toast should animate in.
    /// </summary>
    public event EventHandler? ToastShown;

    /// <summary>
    /// Raised when the toast should animate out.
    /// </summary>
    public event EventHandler? ToastDismissRequested;

    /// <summary>
    /// Raised when the toast dismiss animation completes.
    /// </summary>
    public event EventHandler? DismissAnimationCompleted;

    public ToastOverlayControl()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows the toast by raising the animation event.
    /// </summary>
    public void ShowToast()
    {
        ToastShown?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Dismisses the toast by raising the animation event.
    /// </summary>
    public void DismissToast()
    {
        ToastDismissRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Animates the toast in with slide and fade effects.
    /// </summary>
    public void AnimateIn()
    {
        Dispatcher.Invoke(() =>
        {
            var toastTranslate = FindName("ToastTranslate") as TranslateTransform;
            if (toastTranslate == null) return;

            Visibility = Visibility.Visible;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            toastTranslate.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(60, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
            BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        });
    }

    /// <summary>
    /// Animates the toast out with slide and fade effects.
    /// </summary>
    public void AnimateOut()
    {
        Dispatcher.Invoke(() =>
        {
            var toastTranslate = FindName("ToastTranslate") as TranslateTransform;
            if (toastTranslate == null) return;

            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var slideOut = new DoubleAnimation(0, 60, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = ease,
            };
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
            fadeOut.Completed += (_, _) =>
            {
                Visibility = Visibility.Collapsed;
                Opacity = 0;
                toastTranslate.Y = 60;
                DismissAnimationCompleted?.Invoke(this, EventArgs.Empty);
            };
            toastTranslate.BeginAnimation(TranslateTransform.YProperty, slideOut);
            BeginAnimation(OpacityProperty, fadeOut);
        });
    }
}
