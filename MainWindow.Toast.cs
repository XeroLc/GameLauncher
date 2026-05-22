using System;
using System.Linq;
using GameLauncher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace GameLauncher
{
    public sealed partial class MainWindow : Window
    {
        private DispatcherTimer _toastTimer;

        private void ShowScanToast(AutoScanResult result)
        {
            if (ScanToastPanel == null || ScanToastMessage == null || ScanToastBorder == null)
                return;

            var gameNames = result.NewGameNames.Take(3).ToList();
            var message = gameNames.Count switch
            {
                1 => $"发现新游戏「{gameNames[0]}」，已自动添加到库中。",
                2 => $"发现新游戏「{gameNames[0]}」和「{gameNames[1]}」，已自动添加到库中。",
                _ => $"发现「{gameNames[0]}」等 {result.NewGamesFound} 个新游戏，已自动添加到库中。"
            };
            ScanToastMessage.Text = message;

            ScanToastPanel.Visibility = Visibility.Visible;

            var storyboard = new Storyboard();

            var opacityAnim = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(300)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(opacityAnim, ScanToastBorder);
            Storyboard.SetTargetProperty(opacityAnim, "Opacity");
            storyboard.Children.Add(opacityAnim);

            var translateAnim = new DoubleAnimation
            {
                From = 20,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(400)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(translateAnim, ScanToastTransform);
            Storyboard.SetTargetProperty(translateAnim, "TranslateX");
            storyboard.Children.Add(translateAnim);

            var scaleAnim = new DoubleAnimation
            {
                From = 0.95,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(400)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(scaleAnim, ScanToastTransform);
            Storyboard.SetTargetProperty(scaleAnim, "ScaleX");
            storyboard.Children.Add(scaleAnim);

            var scaleYAnim = new DoubleAnimation
            {
                From = 0.95,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(400)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(scaleYAnim, ScanToastTransform);
            Storyboard.SetTargetProperty(scaleYAnim, "ScaleY");
            storyboard.Children.Add(scaleYAnim);

            storyboard.Begin();

            if (_toastTimer != null)
            {
                _toastTimer.Stop();
                _toastTimer.Tick -= ToastTimer_Tick;
            }

            _toastTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(8)
            };
            _toastTimer.Tick += ToastTimer_Tick;
            _toastTimer.Start();
        }

        private void ToastTimer_Tick(object sender, object e)
        {
            HideScanToast();
        }

        private void HideScanToast()
        {
            if (_toastTimer != null)
            {
                _toastTimer.Stop();
                _toastTimer.Tick -= ToastTimer_Tick;
                _toastTimer = null;
            }

            if (ScanToastPanel == null) return;

            var storyboard = new Storyboard();

            var opacityAnim = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(250)),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(opacityAnim, ScanToastBorder);
            Storyboard.SetTargetProperty(opacityAnim, "Opacity");
            storyboard.Children.Add(opacityAnim);

            storyboard.Completed += (s, args) =>
            {
                ScanToastPanel.Visibility = Visibility.Collapsed;
            };
            storyboard.Begin();
        }
    }
}