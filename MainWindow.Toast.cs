using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace GameLauncher
{
    public enum ToastType
    {
        Info,
        Success,
        Warning,
        Error,
        Confirm
    }

    public sealed partial class MainWindow : Window
    {
        private class ToastItem
        {
            public string Title { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
            public ToastType Type { get; set; }
            public Action<bool>? OnConfirm { get; set; }
        }

        private bool _isToastShowing;
        private ToastType _currentToastType;
        private readonly Queue<ToastItem> _toastQueue = new();
        private DispatcherTimer? _toastTimer;
        private Action<bool>? _currentOnConfirm;

        public void ShowToast(string title, string message, ToastType type, Action<bool>? onConfirm = null)
        {
            var item = new ToastItem
            {
                Title = title,
                Message = message,
                Type = type,
                OnConfirm = onConfirm
            };

            if (_isToastShowing)
            {
                // 如果当前显示的是 Confirm 类型，入队等待
                if (_currentToastType == ToastType.Confirm)
                {
                    _toastQueue.Enqueue(item);
                    return;
                }
                // 否则新通知立即替换旧通知
                DismissImmediate();
            }

            ShowToastInternal(item);
        }

        /// <summary>
        /// 立即隐藏当前 Toast（无动画），用于被新通知替换
        /// </summary>
        private void DismissImmediate()
        {
            StopTimer();
            _isToastShowing = false;
            if (ToastPanel != null)
                ToastPanel.Visibility = Visibility.Collapsed;
        }

        private void ShowToastInternal(ToastItem item)
        {
            if (ToastPanel == null || ToastBorder == null || ToastIcon == null ||
                ToastTitle == null || ToastMessage == null || ToastTransform == null ||
                ToastCloseButton == null || ToastConfirmPanel == null ||
                ToastConfirmButton == null || ToastCancelButton == null ||
                ToastColorStrip == null)
                return;

            _isToastShowing = true;
            _currentToastType = item.Type;

            // Set colors and icons based on type
            var (color, glyph) = item.Type switch
            {
                ToastType.Info => (GetAccentColor(), "\uE946"),
                ToastType.Success => (ColorFromHex("#4CAF50"), "\uE8FB"),
                ToastType.Warning => (ColorFromHex("#FF9800"), "\uE814"),
                ToastType.Error => (ColorFromHex("#F44336"), "\uE783"),
                ToastType.Confirm => (GetAccentColor(), "\uE9CE"),
                _ => (GetAccentColor(), "\uE946")
            };

            var brush = new SolidColorBrush(color);
            ToastIcon.Foreground = brush;
            ToastIcon.Glyph = glyph;
            ToastColorStrip.Background = brush;
            ToastTitle.Text = item.Title;
            ToastMessage.Text = item.Message;

            // Toggle confirm vs close button visibility
            if (item.Type == ToastType.Confirm)
            {
                ToastConfirmPanel.Visibility = Visibility.Visible;
                ToastCloseButton.Visibility = Visibility.Collapsed;
                _currentOnConfirm = item.OnConfirm;
            }
            else
            {
                ToastConfirmPanel.Visibility = Visibility.Collapsed;
                ToastCloseButton.Visibility = Visibility.Visible;
            }

            // Show panel
            ToastPanel.Visibility = Visibility.Visible;

            // Animate in — slide from right + fade + scale
            var storyboard = new Storyboard();

            var opacityAnim = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(280)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(opacityAnim, ToastBorder);
            Storyboard.SetTargetProperty(opacityAnim, "Opacity");
            storyboard.Children.Add(opacityAnim);

            var translateAnim = new DoubleAnimation
            {
                From = 30,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(350)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(translateAnim, ToastTransform);
            Storyboard.SetTargetProperty(translateAnim, "TranslateX");
            storyboard.Children.Add(translateAnim);

            var scaleXAnim = new DoubleAnimation
            {
                From = 0.92,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(350)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(scaleXAnim, ToastTransform);
            Storyboard.SetTargetProperty(scaleXAnim, "ScaleX");
            storyboard.Children.Add(scaleXAnim);

            var scaleYAnim = new DoubleAnimation
            {
                From = 0.92,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(350)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(scaleYAnim, ToastTransform);
            Storyboard.SetTargetProperty(scaleYAnim, "ScaleY");
            storyboard.Children.Add(scaleYAnim);

            storyboard.Begin();

            // Auto-dismiss for non-Confirm types
            if (item.Type != ToastType.Confirm)
            {
                StopTimer();
                _toastTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(8)
                };
                _toastTimer.Tick += ToastTimer_Tick;
                _toastTimer.Start();
            }
        }

        private void ToastTimer_Tick(object? sender, object e)
        {
            DismissToast();
        }

        private void DismissToast()
        {
            StopTimer();

            if (ToastPanel == null || ToastBorder == null || ToastTransform == null)
                return;

            var storyboard = new Storyboard();

            // Slide out to right
            var translateAnim = new DoubleAnimation
            {
                From = 0,
                To = 30,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(translateAnim, ToastTransform);
            Storyboard.SetTargetProperty(translateAnim, "TranslateX");
            storyboard.Children.Add(translateAnim);

            // Fade out
            var opacityAnim = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(opacityAnim, ToastBorder);
            Storyboard.SetTargetProperty(opacityAnim, "Opacity");
            storyboard.Children.Add(opacityAnim);

            storyboard.Completed += (s, args) =>
            {
                ToastPanel.Visibility = Visibility.Collapsed;
                _isToastShowing = false;

                // Show next queued toast
                if (_toastQueue.Count > 0)
                {
                    var nextItem = _toastQueue.Dequeue();
                    ShowToastInternal(nextItem);
                }
            };
            storyboard.Begin();
        }

        private void ToastCloseButton_Click(object sender, RoutedEventArgs e)
        {
            DismissToast();
        }

        private void ToastConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            _currentOnConfirm?.Invoke(true);
            DismissToast();
        }

        private void ToastCancelButton_Click(object sender, RoutedEventArgs e)
        {
            _currentOnConfirm?.Invoke(false);
            DismissToast();
        }

        private void StopTimer()
        {
            if (_toastTimer != null)
            {
                _toastTimer.Stop();
                _toastTimer.Tick -= ToastTimer_Tick;
                _toastTimer = null;
            }
        }

        private static Windows.UI.Color GetAccentColor()
        {
            if (Application.Current.Resources.TryGetValue("SystemAccentColor", out var value) &&
                value is Windows.UI.Color accentColor)
            {
                return accentColor;
            }
            return Windows.UI.Color.FromArgb(255, 0, 120, 212);
        }

        private static Windows.UI.Color ColorFromHex(string hex)
        {
            hex = hex.TrimStart('#');
            byte a = 255, r = 0, g = 0, b = 0;
            if (hex.Length == 8)
            {
                a = Convert.ToByte(hex.Substring(0, 2), 16);
                r = Convert.ToByte(hex.Substring(2, 2), 16);
                g = Convert.ToByte(hex.Substring(4, 2), 16);
                b = Convert.ToByte(hex.Substring(6, 2), 16);
            }
            else if (hex.Length == 6)
            {
                r = Convert.ToByte(hex.Substring(0, 2), 16);
                g = Convert.ToByte(hex.Substring(2, 2), 16);
                b = Convert.ToByte(hex.Substring(4, 2), 16);
            }
            return Windows.UI.Color.FromArgb(a, r, g, b);
        }
    }
}