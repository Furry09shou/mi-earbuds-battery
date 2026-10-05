using Windows.Foundation;
using Windows.Media.Control;

namespace RonghuiEarbuds.App.Core;

/// <summary>当前系统媒体会话信息：曲目标题与播放/暂停状态。</summary>
public sealed record MediaInfo(string? Title, string? Artist, bool Playing);

/// <summary>
/// 系统媒体会话（SMTC）监听：同步当前正在播放的内容与状态，
/// 供悬浮条媒体栏显示曲目与真实播放/暂停图标。
/// net10.0-windows TFM 自带 WinRT 投影（Windows.Media.Control），零额外依赖；
/// 事件在后台线程触发，App 侧自行 Dispatcher 调度。
/// </summary>
public sealed class MediaSession : IDisposable
{
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs> _onCurrent;
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs> _onSessions;
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> _onProps;
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> _onPlayback;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    public MediaSession()
    {
        _onCurrent = (m, args) => Attach(m.GetCurrentSession());
        _onSessions = (m, args) => Refresh();
        _onProps = (s, args) => { _ = EmitAsync(s); };
        _onPlayback = (s, args) => { _ = EmitAsync(s); };
    }

    /// <summary>播放内容或状态变化（后台线程触发，订阅方需自行切到 UI 线程）。</summary>
    public event Action<MediaInfo>? Changed;

    /// <summary>初始化 SMTC 管理器并绑定当前会话（必须在 STA/UI 线程调用）。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += _onCurrent;
            _manager.SessionsChanged += _onSessions;
            Attach(_manager.GetCurrentSession());
        }
        catch
        {
            // 精简系统/获取失败：悬浮条媒体按钮仍可用（全局媒体键），只是没有曲目同步
            _manager = null;
        }
    }

    private void Refresh()
    {
        try { Attach(_manager?.GetCurrentSession()); }
        catch { /* 会话瞬间切换的竞态：忽略 */ }
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(_session, session)) return;
        if (_session is { } old)
        {
            old.MediaPropertiesChanged -= _onProps;
            old.PlaybackInfoChanged -= _onPlayback;
        }
        _session = session;
        if (_session is { } cur)
        {
            cur.MediaPropertiesChanged += _onProps;
            cur.PlaybackInfoChanged += _onPlayback;
        }
        _ = EmitAsync(session);
    }

    private async Task EmitAsync(GlobalSystemMediaTransportControlsSession? session)
    {
        MediaInfo info;
        try
        {
            if (session is null)
            {
                info = new MediaInfo(null, null, false);
            }
            else
            {
                var props = await session.TryGetMediaPropertiesAsync();
                var playback = session.GetPlaybackInfo();
                info = new MediaInfo(props?.Title, props?.Artist,
                    playback?.PlaybackStatus ==
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
            }
        }
        catch
        {
            info = new MediaInfo(null, null, false);
        }
        Changed?.Invoke(info);
    }

    public void Dispose()
    {
        if (_manager is { } m)
        {
            try
            {
                m.CurrentSessionChanged -= _onCurrent;
                m.SessionsChanged -= _onSessions;
            }
            catch { /* ignore */ }
        }
        Attach(null);
        _manager = null;
    }
}