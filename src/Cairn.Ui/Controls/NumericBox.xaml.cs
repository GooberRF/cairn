using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Cairn.Ui.Controls;

/// <summary>
/// A small number entry with a unit suffix and up/down steppers (ported from the earlier ATX tool, where it
/// was integer-only; here it carries <see cref="Decimals"/> so frames, seconds and float header fields
/// use the same control). Mouse wheel and the arrow keys step it, and a run of steps counts as one
/// gesture: <see cref="InteractionStarted"/> fires on the first step and <see cref="InteractionEnded"/>
/// once the user stops, which is what lets the document coalesce a whole wheel-spin into a single
/// undo step. A typed value commits on Enter or on losing focus.
/// </summary>
public partial class NumericBox : UserControl
{
    private readonly DispatcherTimer _idle;
    private readonly DispatcherTimer _invalidFlash;
    private bool _interacting;
    private bool _updatingText;

    public NumericBox()
    {
        InitializeComponent();
        _idle = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(600),
        };
        _idle.Tick += (_, _) => EndInteraction();

        _invalidFlash = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(1200),
        };
        _invalidFlash.Tick += (_, _) => ClearInvalid();

        Entry.TextChanged += OnTextChanged;
        Entry.LostFocus += (_, _) => { CommitText(); EndInteraction(); };
        Entry.PreviewKeyDown += OnEntryKeyDown;
        UpButton.Click += (_, _) => StepBy(+1);
        DownButton.Click += (_, _) => StepBy(-1);
        PreviewMouseWheel += OnWheel;
        // Screen readers land on the inner text box, so it carries the control's name and help.
        Loaded += (_, _) =>
        {
            AutomationProperties.SetName(Entry, AutomationProperties.GetName(this));
            if (ToolTip is not null && Entry.ToolTip is null) Entry.ToolTip = ToolTip;
        };
        UpdateText();
    }

    /// <summary>Raised when a stepping gesture begins.</summary>
    public event EventHandler? InteractionStarted;

    /// <summary>Raised once the gesture has finished.</summary>
    public event EventHandler? InteractionEnded;

    /// <summary>
    /// Raised after <see cref="Value"/> changed. Use this rather than <c>DependencyPropertyDescriptor.AddValueChanged</c>,
    /// whose static tracker keeps the box (and everything its handler captures) alive for the rest of the session.
    /// </summary>
    public event EventHandler? ValueChanged;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(NumericBox),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    /// <summary>The current value.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(NumericBox), new PropertyMetadata(double.MinValue));

    /// <summary>Lowest accepted value.</summary>
    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(NumericBox), new PropertyMetadata(double.MaxValue));

    /// <summary>Highest accepted value.</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(NumericBox), new PropertyMetadata(1.0));

    /// <summary>How much one wheel notch or arrow press changes the value (Shift: ten times as much).</summary>
    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(
        nameof(Decimals), typeof(int), typeof(NumericBox), new PropertyMetadata(0, OnValueChanged));

    /// <summary>Digits shown after the decimal point; 0 makes it a whole-number box.</summary>
    public int Decimals
    {
        get => (int)GetValue(DecimalsProperty);
        set => SetValue(DecimalsProperty, value);
    }

    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(
        nameof(Suffix), typeof(string), typeof(NumericBox),
        new PropertyMetadata(string.Empty, OnSuffixChanged));

    /// <summary>The unit shown after the number, e.g. "ticks".</summary>
    public string Suffix
    {
        get => (string)GetValue(SuffixProperty);
        set => SetValue(SuffixProperty, value);
    }

    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
        nameof(IsReadOnly), typeof(bool), typeof(NumericBox), new PropertyMetadata(false, OnReadOnlyChanged));

    /// <summary>Shows the value without letting it be changed (a read-only document).</summary>
    public bool IsReadOnly
    {
        get => (bool)GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public static readonly DependencyProperty IsIndeterminateProperty = DependencyProperty.Register(
        nameof(IsIndeterminate), typeof(bool), typeof(NumericBox), new PropertyMetadata(false, OnValueChanged));

    /// <summary>
    /// True when the box edits several items whose values differ: it shows "mixed" instead of a number
    /// until a value is typed or stepped (stepping starts from <see cref="Value"/>, the first item's).
    /// </summary>
    public bool IsIndeterminate
    {
        get => (bool)GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (NumericBox)d;
        box.UpdateText();
        if (e.Property == ValueProperty) box.ValueChanged?.Invoke(box, EventArgs.Empty);
    }

    private static void OnSuffixChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((NumericBox)d).SuffixText.Text = (string)e.NewValue;

    private static void OnReadOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (NumericBox)d;
        bool readOnly = (bool)e.NewValue;
        box.Entry.IsReadOnly = readOnly;
        box.UpButton.IsEnabled = !readOnly;
        box.DownButton.IsEnabled = !readOnly;
    }

    private string Format(double value) =>
        value.ToString(Decimals <= 0 ? "0" : "0." + new string('#', Math.Clamp(Decimals, 1, 6)), CultureInfo.CurrentCulture);

    private void UpdateText()
    {
        if (_updatingText) return;
        _updatingText = true;
        try
        {
            string text = IsIndeterminate ? string.Empty : Format(Value);
            if (!string.Equals(Entry.Text, text, StringComparison.Ordinal)) Entry.Text = text;
            MixedText.Visibility = IsIndeterminate && Entry.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _updatingText = false; }
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsIndeterminate) MixedText.Visibility = Entry.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_updatingText || Entry.IsFocused) return;
        CommitText();
    }

    /// <summary>
    /// Writes whatever is typed in the box into <see cref="Value"/>. The shell calls this before a
    /// Save or a close, because a window-level shortcut runs without moving focus and this control
    /// commits on losing it.
    /// </summary>
    public void CommitPending()
    {
        CommitText();
        EndInteraction();
    }

    private void CommitText()
    {
        if (_updatingText || IsReadOnly) return;
        const NumberStyles styles = NumberStyles.Float | NumberStyles.AllowThousands;
        string text = Entry.Text;
        if (double.TryParse(text, styles, CultureInfo.CurrentCulture, out double parsed) && double.IsFinite(parsed))
        {
            ClearInvalid();
            double rounded = Math.Round(Math.Clamp(parsed, Minimum, Maximum), Math.Clamp(Decimals, 0, 6));
            if (!Format(rounded).Equals(Format(Value), StringComparison.Ordinal)) Value = rounded;
            else if (IsIndeterminate)
            {
                // The first item already has this value, so the property does not change; push it anyway
                // so every other selected item gets it too.
                Value = rounded;
                GetBindingExpression(ValueProperty)?.UpdateSource();
            }
        }
        else if (text.Trim().Length > 0)
        {
            ShowInvalid();
        }
        else
        {
            ClearInvalid();
        }
        UpdateText();
    }

    /// <summary>Marks the box briefly, so a rejected entry is not simply undone in silence.</summary>
    private void ShowInvalid()
    {
        Chrome.SetResourceReference(Border.BorderBrushProperty, "Severity.Error");
        ToolTipService.SetToolTip(Entry, "That is not a number, so the previous value was kept.");
        _invalidFlash.Stop();
        _invalidFlash.Start();
    }

    private void ClearInvalid()
    {
        _invalidFlash.Stop();
        Chrome.SetResourceReference(Border.BorderBrushProperty, "App.Border");
        ToolTipService.SetToolTip(Entry, ToolTip);
    }

    private void OnEntryKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up: StepBy(+1); e.Handled = true; break;
            case Key.Down: StepBy(-1); e.Handled = true; break;
            case Key.Enter: CommitText(); EndInteraction(); e.Handled = true; break;
            case Key.Escape:
                // Esc first puts a half-typed value back; with nothing to put back it goes on to the
                // dialog (its Cancel button), so Esc still closes a dialog that opened on a number box.
                string typed = Entry.Text;
                UpdateText();
                EndInteraction();
                e.Handled = !string.Equals(typed, Entry.Text, StringComparison.Ordinal);
                break;
        }
    }

    /// <summary>
    /// Steps the value only when the box has the keyboard focus. Hovering is not consent: a wheel
    /// that passes over this control on its way down a scrolling panel would otherwise edit the file.
    /// </summary>
    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!IsKeyboardFocusWithin) return;
        StepBy(e.Delta > 0 ? +1 : -1);
        e.Handled = true;
    }

    private void StepBy(int direction)
    {
        if (IsReadOnly) return;
        // A half-typed value is the starting point of the step, not the stale bound one.
        if (!IsIndeterminate || Entry.Text.Length > 0) CommitText();
        BeginInteraction();
        double amount = Step * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1);
        Value = Math.Round(Math.Clamp(Value + direction * amount, Minimum, Maximum), Math.Clamp(Decimals, 0, 6));
        _idle.Stop();
        _idle.Start();
    }

    private void BeginInteraction()
    {
        if (_interacting) return;
        _interacting = true;
        InteractionStarted?.Invoke(this, EventArgs.Empty);
    }

    private void EndInteraction()
    {
        _idle.Stop();
        if (!_interacting) return;
        _interacting = false;
        InteractionEnded?.Invoke(this, EventArgs.Empty);
    }
}
