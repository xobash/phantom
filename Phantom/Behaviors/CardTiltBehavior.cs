using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Phantom.Behaviors;

public static class CardTiltBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(CardTiltBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty TiltStateProperty =
        DependencyProperty.RegisterAttached(
            "TiltState",
            typeof(TiltState),
            typeof(CardTiltBehavior),
            new PropertyMetadata(null));

    private const double MaxSkew = 1.8;
    private const double MaxShift = 1.6;
    private const double MaxTiltWidth = 520;
    private const double MaxTiltHeight = 320;

    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        element.SetValue(IsEnabledProperty, value);
    }

    public static bool GetIsEnabled(DependencyObject element)
    {
        return (bool)element.GetValue(IsEnabledProperty);
    }

    private static void SetTiltState(DependencyObject element, TiltState? state)
    {
        element.SetValue(TiltStateProperty, state);
    }

    private static TiltState? GetTiltState(DependencyObject element)
    {
        return element.GetValue(TiltStateProperty) as TiltState;
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            Attach(element);
            return;
        }

        Detach(element);
    }

    private static void Attach(FrameworkElement element)
    {
        EnsureTiltState(element);
        element.MouseMove -= OnMouseMove;
        element.MouseLeave -= OnMouseLeave;
        element.Unloaded -= OnUnloaded;
        element.MouseMove += OnMouseMove;
        element.MouseLeave += OnMouseLeave;
        element.Unloaded += OnUnloaded;
    }

    private static void Detach(FrameworkElement element)
    {
        element.MouseMove -= OnMouseMove;
        element.MouseLeave -= OnMouseLeave;
        element.Unloaded -= OnUnloaded;

        var state = GetTiltState(element);
        if (state is null)
        {
            return;
        }

        state.Skew.BeginAnimation(SkewTransform.AngleXProperty, null);
        state.Skew.BeginAnimation(SkewTransform.AngleYProperty, null);
        state.Translate.BeginAnimation(TranslateTransform.XProperty, null);
        state.Translate.BeginAnimation(TranslateTransform.YProperty, null);
        state.Skew.AngleX = 0;
        state.Skew.AngleY = 0;
        state.Translate.X = 0;
        state.Translate.Y = 0;
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            Detach(element);
        }
    }

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        var state = EnsureTiltState(element);
        if (!ShouldTilt(element))
        {
            ResetAnimated(state);
            return;
        }

        var point = e.GetPosition(element);
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return;
        }

        var nx = ((point.X / element.ActualWidth) - 0.5) * 2;
        var ny = ((point.Y / element.ActualHeight) - 0.5) * 2;

        state.Skew.BeginAnimation(SkewTransform.AngleXProperty, null);
        state.Skew.BeginAnimation(SkewTransform.AngleYProperty, null);
        state.Translate.BeginAnimation(TranslateTransform.XProperty, null);
        state.Translate.BeginAnimation(TranslateTransform.YProperty, null);

        state.Skew.AngleX = Math.Clamp(-ny * MaxSkew, -MaxSkew, MaxSkew);
        state.Skew.AngleY = Math.Clamp(nx * MaxSkew, -MaxSkew, MaxSkew);
        state.Translate.X = Math.Clamp(nx * MaxShift, -MaxShift, MaxShift);
        state.Translate.Y = Math.Clamp(ny * MaxShift, -MaxShift, MaxShift);
    }

    private static void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        var state = GetTiltState(element);
        if (state is null)
        {
            return;
        }

        ResetAnimated(state);
    }

    private static void ResetAnimated(TiltState state)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(170));
        var easing = new SineEase { EasingMode = EasingMode.EaseOut };

        state.Skew.BeginAnimation(SkewTransform.AngleXProperty, new DoubleAnimation(0, duration) { EasingFunction = easing });
        state.Skew.BeginAnimation(SkewTransform.AngleYProperty, new DoubleAnimation(0, duration) { EasingFunction = easing });
        state.Translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, duration) { EasingFunction = easing });
        state.Translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, duration) { EasingFunction = easing });
    }

    private static TiltState EnsureTiltState(FrameworkElement element)
    {
        var state = GetTiltState(element);
        if (state is not null)
        {
            return state;
        }

        var skew = new SkewTransform(0, 0);
        var translate = new TranslateTransform(0, 0);

        TransformGroup group;
        if (element.RenderTransform is TransformGroup existingGroup)
        {
            group = existingGroup;
        }
        else
        {
            group = new TransformGroup();
            if (element.RenderTransform is Transform currentTransform)
            {
                if (currentTransform is MatrixTransform matrixTransform)
                {
                    if (!matrixTransform.Matrix.IsIdentity)
                    {
                        group.Children.Add(currentTransform);
                    }
                }
                else
                {
                    group.Children.Add(currentTransform);
                }
            }

            element.RenderTransform = group;
        }

        group.Children.Add(skew);
        group.Children.Add(translate);
        element.RenderTransformOrigin = new Point(0.5, 0.5);

        state = new TiltState(skew, translate);
        SetTiltState(element, state);
        return state;
    }

    private static bool ShouldTilt(FrameworkElement element)
    {
        return element.ActualWidth > 0 &&
               element.ActualHeight > 0 &&
               element.ActualWidth <= MaxTiltWidth &&
               element.ActualHeight <= MaxTiltHeight &&
               element.IsVisible &&
               element.IsEnabled;
    }

    private sealed class TiltState
    {
        public TiltState(SkewTransform skew, TranslateTransform translate)
        {
            Skew = skew;
            Translate = translate;
        }

        public SkewTransform Skew { get; }
        public TranslateTransform Translate { get; }
    }
}
