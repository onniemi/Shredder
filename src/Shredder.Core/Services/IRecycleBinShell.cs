using System.Runtime.InteropServices;

namespace Shredder.Core.Services;

/// <summary>
/// 抽象 Win32 <c>SHEmptyRecycleBin</c>调用,便于在单元测试中替换为 fake,
/// 同时保留默认实现对真实 shell32 的调用路径。
/// </summary>
public interface IRecycleBinShell
{
    /// <summary>返回 HRESULT:0(S_OK)、1(S_FALSE,回收站已空)、其它视为错误。</summary>
    int Empty();
}

/// <summary>默认实现:调用 <c>shell32!SHEmptyRecycleBin</c>(静默清理元数据)。</summary>
internal sealed class DefaultRecycleBinShell : IRecycleBinShell
{
    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI   = 0x00000002;
    private const uint SHERB_NOSOUND        = 0x00000004;

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    public int Empty() =>
        SHEmptyRecycleBin(IntPtr.Zero, null,
            SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
}
