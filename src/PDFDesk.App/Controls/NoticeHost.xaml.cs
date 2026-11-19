using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace PDFDesk.Controls;

/// <summary>
/// The app's one notice surface: a compact floating card over whatever it is
/// placed on top of.
///
/// Two instances exist — one inside each <see cref="DocumentView"/>, bounded to
/// the page area so it can never reach the sidebar, and one at window level for
/// messages that arrive with no document open (startup problems, a missing
/// recent file, background failures).
/// </summary>
public sealed partial class NoticeHost : UserControl
{
    /// <summary>
    /// Notices the user has closed, by key. A notice describes a property of
    /// the document (it is signed; it is an XFA form) rather than an event, so
    /// re-announcing it after every page edit or tab switch is nagging — but
    /// the host is per-DocumentView, so closing and reopening the tab reasonably
    /// shows it again.
    /// </summary>
    private readonly HashSet<string> _dismissed = [];
    private readonly DispatcherQueueTimer _dismissTimer;
    private readonly UISettings _settings = new();
    private Storyboard? _transition;
    private string? _currentKey;
    private InfoBarSeverity _severity;
    private FontIcon? _visibleIcon;
    private bool _pointerOverCard;
    private bool _dismissing;

    public NoticeHost()
    {
        InitializeComponent();
        _dismissTimer = DispatcherQueue.CreateTimer();
        _dismissTimer.IsRepeating = false;
        _dismissTimer.Interval = TimeSpan.FromSeconds(5);
        _dismissTimer.Tick += DismissTimer_Tick;
        Loaded += (_, _) => RestartDismissTimer();
        Unloaded += NoticeHost_Unloaded;
    }

    private void NoticeHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double availableWidth = e.NewSize.Width - Card.Margin.Left - Card.Margin.Right;
        Card.Width = Math.Clamp(availableWidth, 0, Card.MaxWidth);
    }

    /// <summary>
    /// Shows a notice. Returns false when <paramref name="dismissKey"/> names a
    /// notice the user has already closed, in which case nothing is shown.
    /// </summary>
    /// <param name="title">Short lead-in, or null for a message-only notice.</param>
    /// <param name="dismissKey">
    /// Identity for "don't show me this again". Null means transient — always
    /// shown, and never remembered.
    /// </param>
    public bool Show(string? title, string message, InfoBarSeverity severity, string? dismissKey = null)
    {
        if (dismissKey is not null && _dismissed.Contains(dismissKey))
        {
            return false;
        }

        bool entering = Card.Visibility != Visibility.Visible || _dismissing;
        string heading = title ?? string.Empty;
        bool announce = entering || _severity != severity
            || NoticeTitle.Text != heading || NoticeMessage.Text != message;
        if (dismissKey is not null && dismissKey == _currentKey && !announce) return true;
        _currentKey = dismissKey;
        _dismissing = false;
        _severity = severity;
        if (_visibleIcon is not null)
        {
            _visibleIcon.Visibility = Visibility.Collapsed;
        }
        _visibleIcon = severity switch
        {
            InfoBarSeverity.Success => SuccessIcon,
            InfoBarSeverity.Warning => WarningIcon,
            InfoBarSeverity.Error => ErrorIcon,
            _ => InformationIcon
        };
        _visibleIcon.Visibility = Visibility.Visible;
        NoticeTitle.Text = heading;
        NoticeTitle.Visibility = heading.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoticeMessage.Text = message;
        Card.Visibility = Visibility.Visible;

        if (announce)
        {
            string announcement = string.IsNullOrEmpty(title) ? message : $"{title}. {message}";
            FrameworkElementAutomationPeer.CreatePeerForElement(NoticeMessage)?.RaiseNotificationEvent(
                AutomationNotificationKind.Other,
                severity is InfoBarSeverity.Warning or InfoBarSeverity.Error
                    ? AutomationNotificationProcessing.ImportantMostRecent
                    : AutomationNotificationProcessing.MostRecent,
                announcement, nameof(NoticeHost));
        }

        if (entering)
        {
            if (_settings.AnimationsEnabled)
            {
                Animate(0, 1, -8, 0, TimeSpan.FromMilliseconds(180), EasingMode.EaseOut);
            }
            else
            {
                StopTransition();
                Card.Opacity = 1;
                CardOffset.Y = 0;
            }
        }
        RestartDismissTimer();
        return true;
    }

    /// <summary>Hides the notice without recording a dismissal.</summary>
    public void Clear()
    {
        _dismissTimer.Stop();
        StopTransition();
        _dismissing = false;
        _pointerOverCard = false;
        _currentKey = null;
        Card.Visibility = Visibility.Collapsed;
    }

    private void DismissTimer_Tick(object sender, object args) => Dismiss();

    internal void Close()
    {
        Clear();
        _dismissTimer.Tick -= DismissTimer_Tick;
    }

    private void RestartDismissTimer()
    {
        _dismissTimer.Stop();
        if (_pointerOverCard || _dismissing || Card.Visibility != Visibility.Visible)
        {
            return;
        }

        _dismissTimer.Start();
    }

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverCard = true;
        _dismissTimer.Stop();
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverCard = false;
        RestartDismissTimer();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
    }

    private void Dismiss()
    {
        if (_dismissing || Card.Visibility != Visibility.Visible)
        {
            return;
        }

        if (_currentKey is not null) _dismissed.Add(_currentKey);
        _dismissTimer.Stop();
        if (!_settings.AnimationsEnabled)
        {
            Clear();
            return;
        }

        _dismissing = true;
        Animate(Card.Opacity, 0, CardOffset.Y, -6, TimeSpan.FromMilliseconds(130), EasingMode.EaseIn);
    }

    private void Animate(double opacityFrom, double opacityTo, double yFrom, double yTo,
        TimeSpan duration, EasingMode easingMode)
    {
        StopTransition();
        Card.Opacity = opacityTo;
        CardOffset.Y = yTo;

        var fade = new DoubleAnimation
        {
            From = opacityFrom,
            To = opacityTo,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = easingMode }
        };
        Storyboard.SetTarget(fade, Card);
        Storyboard.SetTargetProperty(fade, nameof(Opacity));

        var slide = new DoubleAnimation
        {
            From = yFrom,
            To = yTo,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = easingMode }
        };
        Storyboard.SetTarget(slide, CardOffset);
        Storyboard.SetTargetProperty(slide, nameof(CardOffset.Y));

        var transition = new Storyboard();
        transition.Children.Add(fade);
        transition.Children.Add(slide);
        transition.Completed += (_, _) =>
        {
            if (_transition != transition)
            {
                return;
            }
            if (_dismissing)
            {
                Clear();
            }
            else
            {
                StopTransition();
            }
        };
        _transition = transition;
        transition.Begin();
    }

    private void StopTransition()
    {
        var transition = _transition;
        _transition = null;
        transition?.Stop();
    }

    private void NoticeHost_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_currentKey is null || _dismissing)
        {
            Clear();
            return;
        }

        _pointerOverCard = false;
        _dismissTimer.Stop();
        StopTransition();
    }
}
