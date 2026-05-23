namespace Shredder.Core.Models;

/// <summary>
/// 回收站清空操作的结构化结果。让调用方(CLI/GUI/审计)清楚地知道:
/// 枚举到多少项、成功/失败/跳过各多少,以及 Shell32 SHEmptyRecycleBin 的 HRESULT。
/// </summary>
/// <remarks>
/// <para>调用方可据此区分:</para>
/// <list type="bullet">
///   <item>整体成功:<see cref="OverallSucceeded"/>= true(<see cref="Failed"/>= 0 且 shell 返回 S_OK/S_FALSE 或被跳过)。</item>
///   <item>部分失败:<see cref="Failed"/>&gt; 0,但其它项仍被正常处理;<see cref="FailedItems"/>给出脱敏后的明细。</item>
///   <item>shell 失败:<see cref="ShellHResult"/>不为 null 且不是 0/1。</item>
/// </list>
/// </remarks>
public sealed class RecycleBinEmptyResult
{
    /// <summary>枚举出的候选条目总数(等于 <see cref="Succeeded"/>+ <see cref="Failed"/>+ <see cref="Skipped"/>)。</summary>
    public int TotalCandidates { get; init; }

    /// <summary>实际粉碎成功的条目数。</summary>
    public int Succeeded { get; init; }

    /// <summary>抛异常并被记录的条目数。</summary>
    public int Failed { get; init; }

    /// <summary>因配置(<c>OverwriteContents</c>=false)或前置过滤被跳过、未尝试粉碎的条目数。</summary>
    public int Skipped { get; init; }

    /// <summary>SHEmptyRecycleBin 返回的 HRESULT;<see langword="null"/>表示未调用 shell(配置关闭)。</summary>
    public int? ShellHResult { get; init; }

    /// <summary>所有失败条目的脱敏明细。永不包含原始路径,使用 <c>PathHasher.Hash</c> 后的占位符。</summary>
    public IReadOnlyList<RecycleBinFailedItem> FailedItems { get; init; } = Array.Empty<RecycleBinFailedItem>();

    /// <summary>shell 调用是否被视为成功:0(S_OK)/1(S_FALSE,回收站已空) 或未调用 都算成功。</summary>
    public bool ShellSucceeded =>
        ShellHResult is null or 0 or 1;

    /// <summary>整体是否全部成功(无失败条目且 shell 成功)。</summary>
    public bool OverallSucceeded =>
        Failed == 0 && ShellSucceeded;
}

/// <summary>单个失败条目的脱敏记录。</summary>
public sealed class RecycleBinFailedItem
{
    /// <summary>使用 <c>PathHasher.Hash</c> 脱敏后的路径占位符,例如 <c>[hash:abcd1234]</c>。</summary>
    public required string PathRedacted { get; init; }

    /// <summary>失败原因(异常类型名),保持简短便于 UI 展示。</summary>
    public required string Reason { get; init; }

    /// <summary>异常 HResult,便于诊断 Win32 错误码。</summary>
    public int? HResult { get; init; }
}
