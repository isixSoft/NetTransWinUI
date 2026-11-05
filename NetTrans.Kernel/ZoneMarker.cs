namespace NetTrans.Kernel;

/// <summary>
/// Mark-of-the-Web（P2-4）：完成任务时按 Windows Zone.Identifier 语义写来源
/// 标记（ZoneId=3 = 来自 Internet）。写法是 "target:Zone.Identifier"——
/// Windows 上即为 NTFS ADS，POSIX 上是合法同名伴随文件，测试无需跳过。
/// 写失败一律吞掉：来源标记永不影响任务完成。
/// </summary>
public static class ZoneMarker
{
    private const string Content = "[ZoneTransfer]\r\nZoneId=3\r\n";

    public static void Write(KernelOptions options, string targetPath)
    {
        if (!options.MarkOfTheWeb || targetPath.Length == 0) return;

        try
        {
            File.WriteAllText(targetPath + ":Zone.Identifier", Content);
        }
        catch (Exception)
        {
            // 读-only 目录、路径过长等：标记是尽力而为的安全附加项
        }
    }
}
