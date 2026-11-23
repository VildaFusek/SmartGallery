using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace SmartGallery.Views;

internal sealed class VideoPlayback : IDisposable
{
    private readonly MediaSource _source;
    private readonly MediaPlayer _player;
    private readonly Action<string> _reportFailure;
    private readonly DispatcherQueue _dispatcher;
    private bool _disposed;

    public MediaPlayerElement Element { get; }

    public VideoPlayback(StorageFile file, Action<string> reportFailure, bool transportControls = true)
    {
        _reportFailure = reportFailure;
        Element = new MediaPlayerElement { AreTransportControlsEnabled = transportControls };
        _dispatcher = Element.DispatcherQueue;
        _source = MediaSource.CreateFromStorageFile(file);
        Element.Source = _source;
        // The element owns the player and releases it with its native visual.
        _player = Element.MediaPlayer;
        _player.MediaFailed += OnMediaFailed;
    }

    public void Play() => _player.Play();

    public void TogglePause()
    {
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            _player.Pause();
        }
        else
        {
            _player.Play();
        }
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var message = $"Přehrávač Windows video neotevřel. Zkuste FFplay z detailu videa. {args.ErrorMessage}";
        _dispatcher.TryEnqueue(() =>
        {
            if (!_disposed)
            {
                _reportFailure(message);
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _player.MediaFailed -= OnMediaFailed;
        try { _player.Pause(); }
        finally
        {
            try { Element.Source = null; }
            finally
            {
                if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.Low, Release))
                {
                    Release();
                }
            }
        }

        void Release()
        {
            try { _source.Dispose(); }
            catch (Exception exception) { System.Diagnostics.Trace.TraceWarning("MediaSource: {0}", exception.Message); }
        }
    }
}
