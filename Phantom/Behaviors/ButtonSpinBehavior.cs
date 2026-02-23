using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Phantom.Behaviors;

public static class ButtonSpinBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(ButtonSpinBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty SpinStateProperty =
        DependencyProperty.RegisterAttached(
            "SpinState",
            typeof(SpinState),
            typeof(ButtonSpinBehavior),
            new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static SpinState? GetSpinState(DependencyObject obj) => obj.GetValue(SpinStateProperty) as SpinState;

    private static void SetSpinState(DependencyObject obj, SpinState? value) => obj.SetValue(SpinStateProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Button button)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            Attach(button);
            return;
        }

        Detach(button);
    }

    private static void Attach(Button button)
    {
        EnsureSpinState(button);
        button.Click -= OnButtonClick;
        button.Unloaded -= OnButtonUnloaded;
        button.Click += OnButtonClick;
        button.Unloaded += OnButtonUnloaded;
    }

    private static void Detach(Button button)
    {
        button.Click -= OnButtonClick;
        button.Unloaded -= OnButtonUnloaded;
    }

    private static void OnButtonUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        Detach(button);
    }

    private static void OnButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var state = EnsureSpinState(button);
        state.Rotate.BeginAnimation(RotateTransform.AngleProperty, null);
        state.Rotate.Angle = 0;
        state.Rotate.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(360, new Duration(TimeSpan.FromMilliseconds(520)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private static SpinState EnsureSpinState(Button button)
    {
        var existing = GetSpinState(button);
        if (existing is not null)
        {
            return existing;
        }

        var rotate = new RotateTransform(0);
        button.RenderTransformOrigin = new Point(0.5, 0.5);

        if (button.RenderTransform is TransformGroup group)
        {
            group.Children.Add(rotate);
        }
        else if (button.RenderTransform is Transform currentTransform)
        {
            if (currentTransform is MatrixTransform matrixTransform && matrixTransform.Matrix.IsIdentity)
            {
                var onlyRotate = new TransformGroup();
                onlyRotate.Children.Add(rotate);
                button.RenderTransform = onlyRotate;
            }
            else
            {
                var composed = new TransformGroup();
                composed.Children.Add(currentTransform);
                composed.Children.Add(rotate);
                button.RenderTransform = composed;
            }
        }
        else
        {
            var onlyRotate = new TransformGroup();
            onlyRotate.Children.Add(rotate);
            button.RenderTransform = onlyRotate;
        }

        var state = new SpinState(rotate);
        SetSpinState(button, state);
        return state;
    }

    private sealed class SpinState
    {
        public SpinState(RotateTransform rotate)
        {
            Rotate = rotate;
        }

        public RotateTransform Rotate { get; }
    }
}
