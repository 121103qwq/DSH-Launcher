using System.IO;

namespace DshLauncher.Services;

/// <summary>
/// 递归删除工具：先清除只读属性再删。
/// dsh 的 attachments/v1/objects 是内容寻址存储，对象文件带 ReadOnly 属性；
/// <c>Directory.Delete(recursive: true)</c> / <c>File.Delete</c> 遇到只读文件会抛
/// “Access to the path ... is denied”，导致删除版本半途失败、留下半删除状态。
/// 这里统一按“先清属性、失败再清一次重试”的语义删除。
/// </summary>
internal static class FileSystemCleanup
{
    /// <summary>删除文件；只读属性会被清除。删不掉时抛异常，由调用方决定是否容忍。</summary>
    public static void DeleteFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        ClearReadOnlyAttribute(path);
        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException)
        {
            ClearReadOnlyAttribute(path);
            File.Delete(path);
        }
    }

    /// <summary>递归删除目录（含只读文件/只读子目录）；目录不存在时直接返回。</summary>
    public static void DeleteDirectoryRecursive(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        ClearReadOnlyRecursive(path);
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
            ClearReadOnlyRecursive(path);
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>递归清除目录下所有文件与子目录的只读属性（逐目录容错，不因个别目录失败中断）。</summary>
    public static void ClearReadOnlyRecursive(string path)
    {
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(current);
                directories = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                ClearReadOnlyAttribute(file);
            }

            foreach (var directory in directories)
            {
                ClearReadOnlyAttribute(directory);
                stack.Push(directory);
            }
        }
    }

    /// <summary>统计目录下带只读属性的文件数（删除前提示用）。</summary>
    public static int CountReadOnlyFiles(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        var count = 0;
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(current);
                directories = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                try
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReadOnly) != 0)
                    {
                        count++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            foreach (var directory in directories)
            {
                stack.Push(directory);
            }
        }

        return count;
    }

    private static void ClearReadOnlyAttribute(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清不掉时交给删除动作报错。
        }
    }
}
