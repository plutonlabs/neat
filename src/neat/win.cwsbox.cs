using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace neat;

public sealed partial class Win
{
    /// <summary>
    /// The dialog of an install from the Chrome Web Store (win.cws.cs). It draws its own two buttons, Cancel
    /// on the left and the answer on the right, each with the key that presses it: the buttons of a
    /// ContentDialog come in another order and carry no key. It is shown in one of these ways:
    ///   waiting   a turning ring and Cancel (how it starts);
    ///   asking    the two icons, the question, "It can:" with the lines, Cancel and Add to NEAT;
    ///   saying    a heading, a sentence and Close.
    /// Esc closes it. Enter presses the button that has the focus, which is Add to NEAT once the question
    /// is asked.
    /// </summary>
    private sealed class CwsBox
    {
        // The width of what is in the dialog, so that it does not jump when the question comes.
        private const double Wide = 360;

        // How high the list of what the extension can do may be before it scrolls.
        private const double ListHigh = 240;

        private readonly StackPanel _icons = new() { Orientation = Orientation.Horizontal, Spacing = 14, Visibility = Visibility.Collapsed };
        private readonly TextBlock _title = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _text = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
        private readonly TextBlock _note = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        private readonly ProgressRing _ring = new() { Width = 32, Height = 32, IsActive = true, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly ScrollViewer _scroll;
        private readonly Button _no;
        private readonly TextBlock _noWords;
        private readonly TextBlock _addWords;

        public CwsBox(Skin skin, XamlRoot xr)
        {
            Skin = skin;

            _title.Foreground = skin.Fg;
            _text.Foreground = skin.Fg;
            _note.Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);

            _scroll = new ScrollViewer
            {
                MaxHeight = ListHigh,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _text,
                Visibility = Visibility.Collapsed,
            };

            _no = Keyed("ESC", out _noWords);
            Words(_no, _noWords, "Cancel");

            Add = Keyed("ENTER", out _addWords);
            Words(Add, _addWords, "Add to NEAT");
            Add.IsEnabled = false;

            if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var found) && found is Style accent)
                Add.Style = accent;

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0),
            };
            buttons.Children.Add(_no);
            buttons.Children.Add(Add);

            var body = new StackPanel { Width = Wide, Spacing = 12 };
            body.Children.Add(_icons);
            body.Children.Add(_title);
            body.Children.Add(_ring);
            body.Children.Add(_scroll);
            body.Children.Add(_note);
            body.Children.Add(buttons);

            _title.Text = "Getting the extension…";

            Dialog = new ContentDialog
            {
                XamlRoot = xr,
                Content = body,
                RequestedTheme = skin.Dark ? ElementTheme.Dark : ElementTheme.Light,
                Background = skin.Bg,
                Foreground = skin.Fg,
            };

            _no.Click += (s, a) => Dialog.Hide();

            // What comes in the dialog may be ready before it is on screen, so the focus is set again then.
            Dialog.Opened += (s, a) => Focus();

            Dialog.PreviewKeyDown += (s, a) =>
            {
                if (a.Key == VirtualKey.Escape)
                {
                    a.Handled = true;
                    Dialog.Hide();   // refused while the extension is being added
                }
                else if (a.Key == VirtualKey.Enter && a.KeyStatus.WasKeyDown)
                {
                    // A key that is held down repeats; it must not answer a question that has only just appeared.
                    a.Handled = true;
                }
            };
        }

        /// <summary>The window's colours, for what is drawn into the dialog.</summary>
        public Skin Skin { get; }

        public ContentDialog Dialog { get; }

        /// <summary>The button that adds the extension. What a click does is the window's side.</summary>
        public Button Add { get; }

        /// <summary>Asks the question. The arrow points from the extension to NEAT.</summary>
        public void Ask(string name, UIElement neat, UIElement extension, string can)
        {
            Still();

            _icons.Children.Clear();
            _icons.Children.Add(neat);
            _icons.Children.Add(new FontIcon { Glyph = "", FontSize = 16, Foreground = Skin.Dim, VerticalAlignment = VerticalAlignment.Center });
            _icons.Children.Add(extension);
            _icons.Visibility = Visibility.Visible;

            _title.Text = "Add “" + name + "” to NEAT?";
            _text.Text = can;
            _scroll.Visibility = Visibility.Visible;

            Add.IsEnabled = true;
            Focus();
        }

        /// <summary>Says that something is so, with nothing left to answer.</summary>
        public void Say(string title, string sentence)
        {
            Still();

            _icons.Visibility = Visibility.Collapsed;
            _note.Visibility = Visibility.Collapsed;
            _title.Text = title;
            _text.Text = sentence;
            _scroll.Visibility = Visibility.Visible;

            Add.Visibility = Visibility.Collapsed;
            _no.IsEnabled = true;
            Words(_no, _noWords, "Close");
            Focus();
        }

        /// <summary>The extension is being added: neither button can be pressed.</summary>
        public void Adding()
        {
            _note.Visibility = Visibility.Collapsed;
            _no.IsEnabled = false;
            Add.IsEnabled = false;
            Words(Add, _addWords, "Adding…");
        }

        /// <summary>Adding failed. The question stays; again says whether it can still be answered with Add.</summary>
        public void Problem(string why, bool again)
        {
            _note.Text = why;
            _note.Visibility = Visibility.Visible;

            _no.IsEnabled = true;
            Add.IsEnabled = again;
            Words(Add, _addWords, "Add to NEAT");

            if (!again)
            {
                Add.Visibility = Visibility.Collapsed;
                Words(_no, _noWords, "Close");
            }

            Focus();
        }

        private void Still()
        {
            _ring.IsActive = false;
            _ring.Visibility = Visibility.Collapsed;
        }

        private void Focus()
        {
            var on = Add.Visibility == Visibility.Visible && Add.IsEnabled ? Add : _no;
            on.Focus(FocusState.Programmatic);
        }

        // A button that shows its words and, beside them, the key that presses it.
        private static Button Keyed(string key, out TextBlock words)
        {
            words = new TextBlock { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };

            var cap = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 2),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x30, 0x80, 0x80, 0x80)),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = key, FontSize = 10, Opacity = 0.75 },
            };

            var face = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            face.Children.Add(words);
            face.Children.Add(cap);

            return new Button { Content = face, MinWidth = 110, Padding = new Thickness(16, 6, 10, 7) };
        }

        // The words of a button, on its face and for a screen reader.
        private static void Words(Button button, TextBlock face, string text)
        {
            face.Text = text;
            AutomationProperties.SetName(button, text);
        }
    }
}
