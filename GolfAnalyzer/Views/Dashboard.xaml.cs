using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GolfAnalyzer.ViewModels;

namespace GolfAnalyzer.Views
{
    public partial class Dashboard : UserControl
    {
        private bool _video1Ready;
        private bool _video2Ready;

        private bool _video1Ended;
        private bool _video2Ended;

        // Timer to drive cursor updates while playing
        private DispatcherTimer? _cursorTimer;

        public Dashboard()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Unloaded += OnUnloaded;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopCursorTimer();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is INotifyPropertyChanged oldVm)
                oldVm.PropertyChanged -= OnViewModelPropertyChanged;

            if (e.OldValue is DashboardViewModel oldDash)
                oldDash.SeekRelativeRequested -= OnSeekRelativeRequested;

            if (e.NewValue is INotifyPropertyChanged newVm)
                newVm.PropertyChanged += OnViewModelPropertyChanged;

            if (e.NewValue is DashboardViewModel newDash)
                newDash.SeekRelativeRequested += OnSeekRelativeRequested;

            ResetSession();
        }

        private void OnSeekRelativeRequested(TimeSpan delta)
        {
            if (!_video1Ready || !_video2Ready) return;
            if (Media1.Source is null || Media2.Source is null) return;

            // Clamp to [0, duration]
            TimeSpan Clamp(TimeSpan t, TimeSpan max)
            {
                if (t < TimeSpan.Zero) return TimeSpan.Zero;
                if (max > TimeSpan.Zero && t > max) return max - TimeSpan.FromMilliseconds(1);
                return t;
            }

            var dur = Media1.NaturalDuration.HasTimeSpan ? Media1.NaturalDuration.TimeSpan : TimeSpan.Zero;
            var target = Media1.Position + delta;
            var clamped = Clamp(target, dur);

            Media1.Position = clamped;
            Media2.Position = clamped;

            // Update chart cursor immediately
            if (DataContext is DashboardViewModel vm)
                vm.UpdatePlaybackPosition(clamped);
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DashboardViewModel.Video1Source) ||
                e.PropertyName == nameof(DashboardViewModel.Video2Source))
            {
                ResetSession();
            }
            else if (e.PropertyName == nameof(DashboardViewModel.SpeedRatio))
            {
                if (DataContext is DashboardViewModel vm)
                    ApplySpeed(vm.SpeedRatio);
            }
            else if (e.PropertyName == nameof(DashboardViewModel.IsPaused))
            {
                if (DataContext is DashboardViewModel vm)
                {
                    if (vm.IsPaused)
                    {
                        Media1.Pause();
                        Media2.Pause();
                        StopCursorTimer(); // stop driving the cursor
                    }
                    else
                    {
                        // Resume from current position
                        if (_video1Ready && _video2Ready && Media1.Source != null && Media2.Source != null)
                        {
                            ApplySpeed(vm.SpeedRatio);
                            _video1Ended = false;
                            _video2Ended = false;
                            Media1.Play();
                            Media2.Play();
                            StartCursorTimer(); // resume driving the cursor
                        }
                    }
                }
            }
        }

        private void ResetSession()
        {
            _video1Ready = false;
            _video2Ready = false;
            _video1Ended = false;
            _video2Ended = false;

            StopAndRewind(Media1);
            StopAndRewind(Media2);
            StopCursorTimer();

            // Before playback: Pause should be non-clickable and gray
            if (DataContext is DashboardViewModel vm)
            {
                vm.PauseForeground = Brushes.Gray;
                vm.IsPauseEnabled = false;
                vm.IsPaused = false;

                // Hide cursor until playback starts
                vm.ResetCursor();
            }
        }

        private static void StopAndRewind(MediaElement me)
        {
            me.Stop();
            me.Position = TimeSpan.Zero;
        }

        private void Media1_OnMediaOpened(object sender, RoutedEventArgs e)
        {
            _video1Ready = true;
            _video1Ended = false;

            // Sync cursor sweep to the video duration and show at t=0
            if (DataContext is DashboardViewModel vm && Media1.NaturalDuration.HasTimeSpan)
            {
                vm.SyncCursorToDuration(Media1.NaturalDuration.TimeSpan);
                vm.UpdatePlaybackPosition(TimeSpan.Zero);
            }

            TryStartBoth();
        }

        private void Media2_OnMediaOpened(object sender, RoutedEventArgs e)
        {
            _video2Ready = true;
            _video2Ended = false;
            TryStartBoth();
        }

        private void Media1_OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            _video1Ready = false;
            StopCursorTimer();
            if (DataContext is DashboardViewModel vm)
            {
                vm.ResetCursor();

                vm.PlayForeground = Brushes.Orange;
                vm.IsPlayEnabled = true;

                // On failure: Pause should not be clickable
                vm.PauseForeground = Brushes.Gray;
                vm.IsPauseEnabled = false;
                vm.IsPaused = false;
            }
        }

        private void Media2_OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            _video2Ready = false;
            StopCursorTimer();
            if (DataContext is DashboardViewModel vm)
            {
                vm.ResetCursor();

                vm.PlayForeground = Brushes.Orange;
                vm.IsPlayEnabled = true;

                vm.PauseForeground = Brushes.Gray;
                vm.IsPauseEnabled = false;
                vm.IsPaused = false;
            }
        }

        private void TryStartBoth()
        {
            if (_video1Ready && _video2Ready && Media1.Source != null && Media2.Source != null)
            {
                double ratio = 1.0;
                if (DataContext is DashboardViewModel vm)
                    ratio = vm.SpeedRatio;

                ApplySpeed(ratio);

                // Fresh start: start from 0
                Media1.Position = TimeSpan.Zero;
                Media2.Position = TimeSpan.Zero;

                _video1Ended = false;
                _video2Ended = false;

                // During playback: Pause clickable and orange
                if (DataContext is DashboardViewModel vm2)
                {
                    vm2.IsPaused = false;
                    vm2.PauseForeground = Brushes.Orange;
                    vm2.IsPauseEnabled = true;

                    // Ensure cursor shown at start
                    vm2.UpdatePlaybackPosition(TimeSpan.Zero);
                }

                Media1.Play();
                Media2.Play();

                // Start driving the cursor with the video clock
                StartCursorTimer();
            }
        }

        private void ApplySpeed(double ratio)
        {
            Media1.SpeedRatio = ratio;
            Media2.SpeedRatio = ratio;
        }

        private void Media1_OnMediaEnded(object sender, RoutedEventArgs e)
        {
            _video1Ended = true;
            CheckPlaybackCompleted();
        }

        private void Media2_OnMediaEnded(object sender, RoutedEventArgs e)
        {
            _video2Ended = true;
            CheckPlaybackCompleted();
        }

        private void CheckPlaybackCompleted()
        {
            if (_video1Ended && _video2Ended)
            {
                StopCursorTimer();

                if (DataContext is DashboardViewModel vm)
                {
                    // After end: Play enabled/orange; Pause non-clickable/gray
                    vm.PlayForeground = Brushes.Orange;
                    vm.IsPlayEnabled = true;

                    vm.PauseForeground = Brushes.Gray;
                    vm.IsPauseEnabled = false;
                    vm.IsPaused = false;
                }
            }
        }

        private void StartCursorTimer()
        {
            if (_cursorTimer == null)
            {
                _cursorTimer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(16) // ~60Hz
                };
                _cursorTimer.Tick += OnCursorTick;
            }
            if (!_cursorTimer.IsEnabled)
                _cursorTimer.Start();
        }

        private void StopCursorTimer()
        {
            if (_cursorTimer != null && _cursorTimer.IsEnabled)
                _cursorTimer.Stop();
        }

        private void OnCursorTick(object? sender, EventArgs e)
        {
            if (DataContext is not DashboardViewModel vm) return;

            // Use Media1 as the playback clock (both are synced)
            vm.UpdatePlaybackPosition(Media1.Position);
        }
    }
}