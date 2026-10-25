using CommunityToolkit.Mvvm.Input;
using CCNest.App.Services;
using Wpf.Ui.Controls;

namespace CCNest.App.ViewModels;

public sealed record GuideStep(int Number, string Title, string Body, string PageKey, string Action);

public sealed partial class GuideViewModel : PageViewModel
{
    public GuideViewModel(AppServices services) : base(services) { }

    public override string Key => "guide";
    public override string Title => "使用文档";
    public override string Subtitle => "按这五步完成配置。使用完毕，在“概览”结束环境并恢复网络设置；单独应用的 Windows 设置按需恢复。";
    public override SymbolRegular Icon => SymbolRegular.BookOpen24;

    public IReadOnlyList<GuideStep> Steps { get; } = new GuideStep[]
    {
        new(1, "配置网络线路",
            "一级是你自己的上游线路：推荐连接已启动的 Clash / Mihomo 本地端口，也可导入节点或手动填写 HTTP / SOCKS5 代理；能直连二级时可不设一级。\n" +
            "二级使用自建 VPS 上的 HTTP / SOCKS5 代理，或服务商购买的静态家宽 / ISP 代理。VPS 出口通常是机房 IP，不等于住宅 IP。\n" +
            "点击“保存并逐级测试”，通过后核对并设置期望出口 IP；激活环境后，可点“Claude 风险检测”在 ip.net.coffee 查看信任分与泄漏检测，或点“查看出口 IP 质量”在 Scamalytics 查看 Fraud Score。",
            "network", "配置网络线路"),
        new(2, "修改系统环境",
            "建议勾选五项：区域格式、首选语言、时区、国家或地区、Windows 显示语言，选择与出口所在地一致的设置。\n" +
            "点击“预览并立即应用”。显示语言需已安装对应语言包，并注销后生效；重新登录后再继续。\n" +
            "这样应用的设置会一直保持，结束环境时不会自动恢复；需要还原时，在“概览”或“恢复中心”点击恢复。",
            "system", "修改系统环境"),
        new(3, "检查环境",
            "先在“概览”激活 Claude 环境，再到“环境检测”点击“开始检测”，自动包含专属浏览器检测。\n" +
            "确认出口、语言、区域和时区一致；先处理“风险”和“待检测”，再开始使用。",
            "diagnostics", "打开环境检测"),
        new(4, "使用专属浏览器",
            "浏览器语言应与 Windows 和出口地区一致，保存后打开专属浏览器访问 Claude。\n" +
            "修改语言后，关闭专属浏览器所有窗口再打开，并重新检测。登录资料会长期保留。",
            "browser", "打开专属浏览器页面"),
        new(5, "启动 Claude 应用",
            "在“Claude 应用”中，通过 CCNest 启动 Claude Desktop，或打开 Claude Code 专属终端运行命令行。\n" +
            "已运行的客户端或命令行会话，请先完全退出，再通过 CCNest 启动。",
            "claude", "打开 Claude 应用"),
    };

    [RelayCommand]
    private void Go(string? key)
    {
        if (key != null) Services.Navigate(key);
    }
}
