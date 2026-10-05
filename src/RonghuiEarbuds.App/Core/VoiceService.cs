using System.Speech.Synthesis;

namespace RonghuiEarbuds.App.Core;

/// <summary>
/// 电量语音播报：用系统 TTS（System.Speech，WindowsDesktop 运行时自带、零体积）。
/// 按应用语言挑选中文/英文语音；系统没装对应语音时静默降级（不播报、不报错）。
/// </summary>
public sealed class VoiceService : IDisposable
{
    private readonly SpeechSynthesizer? _synth;

    public VoiceService()
    {
        try
        {
            _synth = new SpeechSynthesizer();
            _synth.Rate = 0;   // 稍慢更清晰：-1
            PickVoice();
            L.Changed += PickVoice;   // 切换界面语言后重新选对应语种语音
        }
        catch
        {
            // TTS 不可用（精简系统/服务环境）：保持 Available=false
            TryDispose();
        }
    }

    public bool Available => _synth is not null;

    private void PickVoice()
    {
        if (_synth is null) return;
        var wantZh = L.Lang == "zh";
        InstalledVoice? fallback = null;
        foreach (var v in _synth.GetInstalledVoices())
        {
            var info = v.VoiceInfo;
            fallback ??= v;
            var culture = info.Culture.Name;
            bool match = wantZh
                ? culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                : culture.StartsWith("en", StringComparison.OrdinalIgnoreCase);
            if (match && (!wantZh || culture.Contains("CN", StringComparison.OrdinalIgnoreCase)))
            {
                _synth.SelectVoice(info.Name);
                return;
            }
            if (match) fallback = v;
        }
        // 目标语言没有语音：退而求其次用第一个可用的（不让按钮完全失声）
        if (fallback is not null) _synth.SelectVoice(fallback.VoiceInfo.Name);
    }

    /// <summary>异步播报；正在播报时先打断（电量连续点击要给最新值）。</summary>
    public void Speak(string text)
    {
        if (_synth is null || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            _synth.SpeakAsyncCancelAll();
            _synth.SpeakAsync(text);
        }
        catch { /* 设备被独占等异常：忽略 */ }
    }

    private void TryDispose()
    {
        try { _synth?.Dispose(); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        L.Changed -= PickVoice;
        TryDispose();
    }
}