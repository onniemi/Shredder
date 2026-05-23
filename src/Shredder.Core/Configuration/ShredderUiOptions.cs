namespace Shredder.Core.Configuration;

public sealed class ShredderUiOptions
{
    /// <summary>二次确认需输入的关键字。默认中文「粉碎」。</summary>
    public string ConfirmationKeyword { get; set; } = "粉碎";

    /// <summary>「开始」按钮在二次确认后还需冷静等待的秒数。</summary>
    public int ConfirmationCooldownSeconds { get; set; } = 5;

    /// <summary>主题:System / Light / Dark。</summary>
    public string Theme { get; set; } = "System";
}
