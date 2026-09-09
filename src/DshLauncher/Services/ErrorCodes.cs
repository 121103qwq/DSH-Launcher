namespace DshLauncher.Services;

/// <summary>
/// 错误码目录（借鉴 Ruler4396/dsh-launcher 的 ErrorCodes 设计，MIT）：
/// 用户可见错误与结构化日志共用同一套码，便于用户在 issue 里直接粘贴定位。
/// 约定：E1xxx 运行环境，E2xxx 插件/扩展，E3xxx 网络/代理，E4xxx 体验增强，E9xxx 内部。
/// </summary>
public static class ErrorCodes
{
    // E1xxx 运行环境 / 启动
    public const string E1001 = "E1001"; // 窗口位置记忆读写失败
    public const string E1002 = "E1002"; // WebView2 缓存失效账本读写失败
    public const string E1003 = "E1003"; // 诊断包导出失败
    public const string E1004 = "E1004"; // Launcher 日志写入失败
    public const string E1005 = "E1005"; // 诊断包导出被取消
    public const string E1006 = "E1006"; // 便携版 Node.js 准备失败
    public const string E1008 = "E1008"; // DSh 安装位置移动失败
    public const string E1009 = "E1009"; // exe 同目录不可写，默认安装位置回退
    public const string E1010 = "E1010"; // WebView2 数据目录回退
    public const string E1011 = "E1011"; // 便携数据根不可写，回退默认数据根
    public const string E1012 = "E1012"; // 旧格式 DSh 凭据转换
    public const string E1013 = "E1013"; // 实例环境变量加解密/清理

    // E2xxx 插件 / 扩展
    public const string E2001 = "E2001"; // 插件依赖自检发现异常
    public const string E2002 = "E2002"; // 插件更新检查失败
    public const string E2003 = "E2003"; // 批量更新部分失败
    public const string E2004 = "E2004"; // 插件命令失败后自动重试/镜像恢复
    public const string E2005 = "E2005"; // 插件失败残留清理（回滚）

    // E3xxx 网络 / 代理
    public const string E3001 = "E3001"; // 代理配置无效
    public const string E3002 = "E3002"; // 余额查询失败

    // E4xxx 体验增强
    public const string E4001 = "E4001"; // 浏览器守卫执行失败

    // E9xxx 内部
    public const string E9001 = "E9001"; // 未分类内部错误

    /// <summary>错误码 → 一句话说明（诊断包 errors.txt 汇总用）。</summary>
    public static string Describe(string code) => code switch
    {
        E1001 => "窗口位置/大小记忆读写失败（不影响使用，窗口回到默认位置）。",
        E1002 => "WebView2 缓存失效账本读写失败（可能继续复用旧前端缓存）。",
        E1003 => "诊断包导出失败（日志/数据文件读取异常）。",
        E1004 => "Launcher 日志写入失败（磁盘/权限问题）。",
        E1005 => "诊断包导出被取消。",
        E1006 => "便携版 Node.js 准备失败（下载/解压/校验/替换）。",
        E1008 => "DSh 安装位置移动失败（目录占用/跨盘复制/权限等）。",
        E1009 => "exe 同目录不可写，默认安装位置已回退到用户文档目录下的旧默认位置。",
        E1010 => "exe 同目录不可写，WebView2 数据目录已回退到 %LocalAppData%\\DeepSeek\\launcher\\WebView2。",
        E1011 => "便携数据根（exe 旁 launcher-data 或 DSH_LAUNCHER_DATA_ROOT）不可用，已回退默认数据根。",
        E1012 => "旧格式 DSh 凭据文件（version/records/refs 包装）已转换或转换失败（失败时保持原样）。",
        E1013 => "实例环境变量的敏感值加解密失败或条目非法（已跳过，不影响其它设置）。",
        E2001 => "插件依赖自检发现异常（核心包混入 profile / 依赖缺失等）。",
        E2002 => "插件更新检查失败（registry 不可达或响应异常）。",
        E2003 => "批量更新部分插件失败（其余插件已更新）。",
        E2004 => "插件命令失败后触发了自动重试或 GitHub 镜像恢复。",
        E2005 => "插件失败安装的残留被清理，或残留清理本身失败。",
        E3001 => "代理配置无效（地址或端口不合法，已忽略）。",
        E3002 => "DeepSeek 余额查询失败（网络/凭据问题）。",
        E4001 => "浏览器守卫执行失败（未能枚举或结束浏览器进程）。",
        _ => "未分类错误。"
    };
}
