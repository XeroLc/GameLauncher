using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameLauncher.Models;
using GameLauncher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameLauncher
{
    public sealed partial class MainWindow : Window
    {
        private readonly ObservableCollection<Game> _filteredGames;
        private string? _selectedTagFilter;
        private string _currentSortMode = "CreatedAt";
        private string? _selectedCollectionFilter;
        private DispatcherTimer? _searchDebounceTimer;
        private readonly Dictionary<string, bool> _fileExistsCache = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastFileCacheRefresh = DateTime.MinValue;

        // Pagination fields
        private int _currentPage = 1;
        private int _pageSize = 30;
        private int _totalFilteredCount = 0;
        private string _lastFilterHash = string.Empty;

        public int CurrentPage
        {
            get => _currentPage;
            set { _currentPage = value; UpdatePaginationDisplay(); }
        }

        public int PageSize
        {
            get => _pageSize;
            set { _pageSize = value; _currentPage = 1; ApplyFilters(); }
        }

        public int TotalPages => _totalFilteredCount == 0 ? 1 : (int)Math.Ceiling((double)_totalFilteredCount / _pageSize);

        private static readonly Dictionary<int, string> _pinyinInitials = new()
        {
            // Common gaming-related Chinese characters
            [0x738B] = "w", [0x8005] = "z", [0x8363] = "r", [0x8000] = "y", // 王者荣耀
            [0x82F1] = "y", [0x96C4] = "x", [0x8054] = "l", [0x76DF] = "m", // 英雄联盟
            [0x6218] = "z", [0x6597] = "d", [0x548C] = "h", [0x5E73] = "p", [0x7CBE] = "j", [0x82F1] = "y", // 战斗/和平精英
            [0x68CB] = "q", [0x724C] = "p", [0x9B54] = "m", [0x517D] = "s", [0x4E16] = "s", [0x754C] = "j", // 棋牌/魔兽世界
            [0x68A6] = "m", [0x5E7B] = "h", [0x897F] = "x", [0x6E38] = "y", // 梦幻西游
            [0x526F] = "f", [0x672C] = "b", [0x7F51] = "w", [0x6613] = "y", // 副本/网易
            [0x4E2D] = "z", [0x56FD] = "g", [0x5927] = "d", [0x5C0F] = "x", // 中国大小
            [0x4E00] = "y", [0x4E8C] = "e", [0x4E09] = "s", [0x56DB] = "s", [0x4E94] = "w", // 一二三四五
            [0x516D] = "l", [0x4E03] = "q", [0x516B] = "b", [0x4E5D] = "j", [0x5341] = "s", // 六七八九十
            [0x5251] = "j", [0x4FA0] = "x", [0x4F20] = "c", [0x5947] = "q", [0x5E7B] = "h", // 剑侠传奇幻
            [0x661F] = "x", [0x9645] = "j", [0x660E] = "m", [0x5929] = "t", [0x5730] = "d", // 星际明天地
            [0x706B] = "h", [0x6C34] = "s", [0x98CE] = "f", [0x96F7] = "l", [0x7535] = "d", // 火水风雷电
            [0x5149] = "g", [0x6697] = "a", [0x7EA2] = "h", [0x84DD] = "l", [0x7EFF] = "l", // 光暗红蓝绿
            [0x767D] = "b", [0x9ED1] = "h", [0x91D1] = "j", [0x94F6] = "y", [0x94DC] = "t", // 白黑金银铜
            [0x94C1] = "t", [0x94A2] = "g", [0x77F3] = "s", [0x6728] = "m", [0x571F] = "t", // 铁钢石木土
            [0x9F99] = "l", [0x51E4] = "f", [0x864E] = "h", [0x72FC] = "l", [0x72D7] = "g", // 龙凤虎狼狗
            [0x732B] = "m", [0x9F20] = "s", [0x725B] = "n", [0x9A6C] = "m", [0x7F8A] = "y", // 猫鼠牛马羊
            [0x9E21] = "j", [0x5154] = "t", [0x86C7] = "s", [0x9F9F] = "g", [0x9C7C] = "y", // 鸡兔蛇龟鱼
            [0x795E] = "s", [0x9B3C] = "g", [0x5996] = "y", [0x602A] = "g", [0x4ED9] = "x", // 神鬼妖怪仙
            [0x6B66] = "w", [0x5668] = "q", [0x88C5] = "z", [0x5907] = "b", [0x6280] = "j", // 武器装备技
            [0x80FD] = "n", [0x529B] = "l", [0x901F] = "s", [0x5EA6] = "d", [0x653B] = "g", // 能力速度攻
            [0x9632] = "f", [0x5FA1] = "y", [0x751F] = "s", [0x547D] = "m", [0x6CD5] = "f", // 防御生命法
            [0x672F] = "s", [0x5E1D] = "d", [0x738B] = "w", [0x5723] = "s", [0x6210] = "c", // 术帝王圣成
            [0x7A7A] = "k", [0x6D77] = "h", [0x9646] = "l", [0x5C71] = "s", [0x6CB3] = "h", // 空海陆山河
            [0x65B0] = "x", [0x65E7] = "j", [0x524D] = "q", [0x540E] = "h", [0x5DE6] = "z", // 新旧前后左
            [0x53F3] = "y", [0x4E0A] = "s", [0x4E0B] = "x", [0x8FDB] = "j", [0x9000] = "t", // 右上下进退
            [0x5F00] = "k", [0x5173] = "g", [0x8D77] = "q", [0x59CB] = "s", [0x7ED3] = "j", // 开关起始结
            [0x675F] = "s", [0x7B2C] = "d", [0x5206] = "f", [0x7C7B] = "l", [0x79CD] = "z", // 束第分类种
            [0x5B57] = "z", [0x7B26] = "f", [0x5408] = "h", [0x7403] = "q", [0x5E26] = "d", // 字符合球带
            [0x5E02] = "s", [0x573A] = "c", [0x666F] = "j", [0x89C2] = "g", [0x97F3] = "y", // 市场景观音
            [0x4E50] = "l", [0x7EBF] = "x", [0x8DEF] = "l", [0x5B58] = "c", [0x6863] = "d", // 乐线路存档
            [0x8BBE] = "s", [0x7F6E] = "z", [0x9009] = "x", [0x9879] = "x", [0x76EE] = "m", // 设置选项目
            [0x5F55] = "l", [0x64CD] = "c", [0x4F5C] = "z", [0x5355] = "d", [0x673A] = "j", // 录操作单机
            [0x6A21] = "m", [0x5F0F] = "s", [0x591A] = "d", [0x7F51] = "w", [0x7EDC] = "l", // 模式多网络
            [0x8054] = "l", [0x673A] = "j", [0x672C] = "b", [0x5730] = "d", [0x56FE] = "t", // 联机本地图
            [0x7248] = "b", [0x672C] = "b", [0x5B89] = "a", [0x88C5] = "z", [0x914D] = "p", // 版本安装配
            [0x9996] = "s", [0x9875] = "y", [0x4EBA] = "r", [0x7269] = "w", [0x4E3B] = "z", // 首页人物主
            [0x9898] = "t", [0x89D2] = "j", [0x8272] = "s", [0x58F0] = "s", [0x5E55] = "m", // 题角色声幕
            [0x5168] = "q", [0x5C4F] = "p", [0x7A97] = "c", [0x53E3] = "k", [0x663E] = "x", // 全屏窗口显
            [0x5361] = "k", [0x8F6E] = "l", [0x7EC4] = "z", [0x961F] = "d", [0x670D] = "f", // 卡轮组队服
            [0x52A1] = "w", [0x5668] = "q", [0x5BA2] = "k", [0x6237] = "h", [0x7AEF] = "d", // 务器客户端
            [0x5E10] = "z", [0x53F7] = "h", [0x5BC6] = "m", [0x7801] = "m", [0x767B] = "d", // 账号密码登
            [0x5F55] = "l", [0x6CE8] = "z", [0x518C] = "c", [0x90AE] = "y", [0x7BB1] = "x", // 录注册邮箱
            [0x9A8C] = "y", [0x8BC1] = "z", [0x624B] = "s", [0x673A] = "j", [0x52A8] = "d", // 验证手机动
            [0x6F2B] = "m", [0x753B] = "h", [0x52A8] = "d", [0x6F2B] = "m", [0x6E38] = "y", // 漫画动漫游
            [0x620F] = "x", [0x5267] = "j", [0x60C5] = "q", [0x611F] = "g", [0x4F53] = "t", // 戏剧情感体
            [0x9A8C] = "y", [0x5192] = "m", [0x9669] = "x", [0x6D4B] = "c", [0x8BD5] = "s", // 验冒险测试
            [0x6B63] = "z", [0x5F0F] = "s", [0x53D1] = "f", [0x5E03] = "b", [0x514D] = "m", // 正式发布免
            [0x8D39] = "f", [0x4ED8] = "f", [0x8D2D] = "g", [0x4E70] = "m", [0x5356] = "m", // 费付购买卖
            [0x5546] = "s", [0x5E97] = "d", [0x5E73] = "p", [0x53F0] = "t", [0x8D26] = "z", // 商店平台账
            [0x4EFB] = "r", [0x52A1] = "w", [0x7BA1] = "g", [0x7406] = "l", [0x5668] = "q", // 任务管理器
            [0x5FEB] = "k", [0x6377] = "j", [0x65B9] = "f", [0x5F0F] = "s", [0x4FBF] = "b", // 快捷方式便
            [0x5220] = "s", [0x9664] = "c", [0x6DFB] = "t", [0x52A0] = "j", [0x7F16] = "b", // 删除添加编
            [0x8F91] = "j", [0x4FEE] = "x", [0x6539] = "g", [0x91CD] = "z", [0x65B0] = "x", // 辑修改重新
            [0x590D] = "f", [0x5236] = "z", [0x7C98] = "z", [0x8D34] = "t", [0x526A] = "j", // 复制粘贴剪
            [0x5207] = "q", [0x64A4] = "c", [0x9500] = "x", [0x6062] = "h", [0x590D] = "f", // 切撤销恢复
            [0x641C] = "s", [0x7D22] = "s", [0x67E5] = "c", [0x627E] = "z", [0x770B] = "k", // 搜索查找看
            [0x663E] = "x", [0x793A] = "s", [0x9690] = "y", [0x85CF] = "c", [0x8FC7] = "g", // 显示隐藏过
            [0x6EE4] = "l", [0x6392] = "p", [0x5E8F] = "x", [0x5217] = "l", [0x8868] = "b", // 滤排序列表
            [0x6807] = "b", [0x7B7E] = "q", [0x6CE8] = "z", [0x91CA] = "s", [0x5907] = "b", // 标签注释备
            [0x5FD8] = "w", [0x8BB0] = "j", [0x5F55] = "l", [0x50CF] = "x", [0x56FE] = "t", // 忘记录像图
            [0x7247] = "p", [0x89C6] = "s", [0x9891] = "p", [0x64AD] = "b", [0x653E] = "f", // 片视频播放
            [0x6682] = "z", [0x505C] = "t", [0x7EE7] = "j", [0x7EED] = "x", [0x8DF3] = "t", // 暂停继续跳
            [0x8FC7] = "g", [0x53D6] = "q", [0x6D88] = "x", [0x9001] = "s", [0x51FA] = "c", // 过取消送出
            [0x53E3] = "k", [0x4EE4] = "l", [0x547D] = "m", [0x4EE4] = "l", [0x63A7] = "k", // 口令命令控
            [0x5236] = "z", [0x53F0] = "t", [0x670D] = "f", [0x52A1] = "w", [0x5668] = "q", // 制台服务器
            [0x7F16] = "b", [0x8BD1] = "y", [0x8BED] = "y", [0x8A00] = "y", [0x6587] = "w", // 编译语言文
            [0x4EF6] = "j", [0x8F6F] = "r", [0x4EF6] = "j", [0x5E94] = "y", [0x7528] = "y", // 件软件应用
            [0x7A0B] = "c", [0x5E8F] = "x", [0x4EE3] = "d", [0x7801] = "m", [0x5E93] = "k", // 程序代码库
            [0x5305] = "b", [0x7BA1] = "g", [0x7406] = "l", [0x5DE5] = "g", [0x5177] = "j", // 包管理工具
            [0x9879] = "x", [0x76EE] = "m", [0x6587] = "w", [0x4EF6] = "j", [0x5939] = "j", // 项目文件夹
            [0x8D44] = "z", [0x6E90] = "y", [0x7F16] = "b", [0x8F91] = "j", [0x5668] = "q", // 资源编辑器
            [0x4E3B] = "z", [0x9875] = "y", [0x9996] = "s", [0x9875] = "y", [0x7F51] = "w", // 主页首页网
            [0x7AD9] = "z", [0x6D4F] = "l", [0x89C8] = "l", [0x5668] = "q", [0x6807] = "b", // 站浏览器标
            [0x7B54] = "d", [0x6848] = "a", [0x95EE] = "w", [0x9898] = "t", [0x641C] = "s", // 答案问题搜
            [0x7D22] = "s", [0x5F15] = "y", [0x64CE] = "q", [0x52A9] = "z", [0x624B] = "s", // 索引擎助手
            [0x4E3B] = "z", [0x9898] = "t", [0x80A4] = "f", [0x6A21] = "m", [0x5F0F] = "s", // 主题肤模式
            [0x6DF1] = "s", [0x8272] = "s", [0x6D45] = "q", [0x81EA] = "z", [0x5B9A] = "d", // 深色浅自定
            [0x4E49] = "y", [0x9ED8] = "m", [0x8BA4] = "r", [0x66F4] = "g", [0x65B0] = "x", // 义默认更新
            [0x68C0] = "j", [0x67E5] = "c", [0x5173] = "g", [0x4E8E] = "y", [0x5E2E] = "b", // 检查关于帮
            [0x52A9] = "z", [0x53CD] = "f", [0x9988] = "k", [0x7248] = "b", [0x6743] = "q", // 助反馈版权
            [0x9690] = "y", [0x79C1] = "s", [0x6761] = "t", [0x6B3E] = "k", [0x534F] = "x", // 隐私条款协
            [0x8BAE] = "y", [0x6CE8] = "z", [0x9500] = "x", [0x8D26] = "z", [0x53F7] = "h", // 议注销账号
            [0x9000] = "t", [0x51FA] = "c", [0x767B] = "d", [0x5F55] = "l", [0x6FC0] = "j", // 退出登录激
            [0x6D3B] = "h", [0x6CE8] = "z", [0x518C] = "c", [0x5FD8] = "w", [0x8BB0] = "j", // 活注册忘记
            [0x5BC6] = "m", [0x7801] = "m", [0x4FEE] = "x", [0x6539] = "g", [0x91CD] = "z", // 码修改重
            [0x7F6E] = "z", [0x8F93] = "s", [0x5165] = "r", [0x786E] = "q", [0x8BA4] = "r", // 置输入确认
            [0x53D6] = "q", [0x6D88] = "x", [0x8FD4] = "f", [0x56DE] = "h", [0x4FDD] = "b", // 取消返回保
            [0x5B58] = "c", [0x52A0] = "j", [0x8F7D] = "z", [0x5BFC] = "d", [0x5165] = "r", // 存加载导入
            [0x5BFC] = "d", [0x51FA] = "c", [0x5220] = "s", [0x9664] = "c", [0x6E05] = "q", // 导出删除清
            [0x7A7A] = "k", [0x91CD] = "z", [0x547D] = "m", [0x540D] = "m", [0x79FB] = "y", // 空重命名移
            [0x52A8] = "d", [0x590D] = "f", [0x5236] = "z", [0x7C98] = "z", [0x8D34] = "t", // 动复制粘贴
            [0x5168] = "q", [0x9009] = "x", [0x53CD] = "f", [0x9009] = "x", [0x9875] = "y", // 全选反选页
            [0x9762] = "m", [0x7FFB] = "f", [0x9875] = "y", [0x4E0A] = "s", [0x4E00] = "y", // 面翻页上一
            [0x4E0B] = "x", [0x4E00] = "y", [0x9875] = "y", [0x7B2C] = "d", [0x6761] = "t", // 下一页第条
            [0x6BCF] = "m", [0x6761] = "t", [0x603B] = "z", [0x5171] = "g", [0x6761] = "t", // 每条总共条
            [0x6E38] = "y", [0x620F] = "x", [0x5E93] = "k", [0x540D] = "m", [0x79F0] = "c", // 游戏库名称
            [0x8DEF] = "l", [0x5F84] = "j", [0x63CF] = "m", [0x8FF0] = "s", [0x6807] = "b", // 路径描述标
            [0x7B7E] = "q", [0x56FE] = "t", [0x6807] = "b", [0x9884] = "y", [0x89C8] = "l", // 签图标预览
            [0x542F] = "q", [0x52A8] = "d", [0x6B21] = "c", [0x6570] = "s", [0x65F6] = "s", // 动次数时
            [0x957F] = "c", [0x6DFB] = "t", [0x52A0] = "j", [0x65F6] = "s", [0x95F4] = "j", // 长添加时间
            [0x6700] = "z", [0x8FD1] = "j", [0x8FD0] = "y", [0x884C] = "x", [0x72B6] = "z", // 最近运行状
            [0x6001] = "t", [0x4E2D] = "z", [0x6B63] = "z", [0x5728] = "z", [0x7EBF] = "x", // 态中正在线
            [0x7F16] = "b", [0x8F91] = "j", [0x5220] = "s", [0x9664] = "c", [0x66F4] = "g", // 编辑删除更
            [0x65B0] = "x", [0x7248] = "b", [0x672C] = "b", [0x65E5] = "r", [0x5FD7] = "z", // 新版本日志
            [0x626B] = "s", [0x63CF] = "m", [0x6D4B] = "c", [0x8BD5] = "s", [0x5907] = "b", // 扫描测试备
            [0x4EFD] = "f", [0x8FD8] = "h", [0x539F] = "y", [0x6062] = "h", [0x590D] = "f", // 份还原恢复
            [0x5386] = "l", [0x53F2] = "s", [0x8BB0] = "j", [0x5F55] = "l", [0x7EDF] = "t", // 历史记录统
            [0x8BA1] = "j", [0x6570] = "s", [0x636E] = "j", [0x5E93] = "k", [0x6587] = "w", // 计数据库文
            [0x4EF6] = "j", [0x5939] = "j", [0x6253] = "d", [0x5F00] = "k", [0x5173] = "g", // 件夹打开关
            [0x95ED] = "b", [0x6700] = "z", [0x5C0F] = "x", [0x5316] = "h", [0x6258] = "t", // 闭最小化托
            [0x76D8] = "p", [0x9000] = "t", [0x51FA] = "c", [0x7ED3] = "j", [0x675F] = "s", // 盘退出结束
            [0x91CD] = "z", [0x542F] = "q", [0x5173] = "g", [0x95ED] = "b", [0x66F4] = "g", // 重启关闭更
            [0x65B0] = "x", [0x6D4B] = "c", [0x8BD5] = "s", [0x8FD0] = "y", [0x884C] = "x", // 新测试运行
            [0x8C03] = "t", [0x8BD5] = "s", [0x6A21] = "m", [0x5F0F] = "s", [0x5F00] = "k", // 调试模式开
            [0x53D1] = "f", [0x8005] = "z", [0x6A21] = "m", [0x5F0F] = "s", [0x7A97] = "c", // 发者模式窗
            [0x53E3] = "k", [0x63A7] = "k", [0x5236] = "z", [0x53F0] = "t", [0x547D] = "m", // 口控制台命
            [0x4EE4] = "l", [0x63D0] = "t", [0x793A] = "s", [0x7B26] = "f", [0x8F93] = "s", // 令提示符输
            [0x51FA] = "c", [0x7F16] = "b", [0x7801] = "m", [0x8C03] = "t", [0x8BD5] = "s", // 出编码调试
            [0x9519] = "c", [0x8BEF] = "w", [0x8B66] = "j", [0x544A] = "g", [0x4FE1] = "x", // 错误警告信
            [0x606F] = "x", [0x6210] = "c", [0x529F] = "g", [0x5931] = "s", [0x8D25] = "b", // 息成功失败
            [0x5DF2] = "y", [0x7ECF] = "j", [0x5B8C] = "w", [0x6210] = "c", [0x8FDB] = "j", // 已经完成进
            [0x5EA6] = "d", [0x6761] = "t", [0x6B63] = "z", [0x5728] = "z", [0x5904] = "c", // 度条正在处
            [0x7406] = "l", [0x7B49] = "d", [0x5F85] = "d", [0x8BF7] = "q", [0x7A0D] = "s", // 理等待请稍
            [0x5019] = "h", [0x786E] = "q", [0x5B9A] = "d", [0x53D6] = "q", [0x6D88] = "x", // 候确定取消
            [0x662F] = "s", [0x5426] = "f", [0x9009] = "x", [0x62E9] = "z", [0x5168] = "q", // 是否选择全
            [0x90E8] = "b", [0x5206] = "f", [0x663E] = "x", [0x793A] = "s", [0x9690] = "y", // 部分显示隐
            [0x85CF] = "c", [0x8FC7] = "g", [0x6EE4] = "l", [0x6392] = "p", [0x5E8F] = "x", // 藏过滤排序
            [0x65B9] = "f", [0x5F0F] = "s", [0x5347] = "s", [0x964D] = "j", [0x540D] = "m", // 式升降名
            [0x79F0] = "c", [0x65F6] = "s", [0x95F4] = "j", [0x6B21] = "c", [0x6570] = "s", // 称时间次数
            [0x65F6] = "s", [0x957F] = "c", [0x6536] = "s", [0x85CF] = "c", [0x5939] = "j", // 长收藏夹
            [0x7BA1] = "g", [0x7406] = "l", [0x65B0] = "x", [0x5EFA] = "j", [0x91CD] = "z", // 理新建重
            [0x547D] = "m", [0x540D] = "m", [0x5220] = "s", [0x9664] = "c", [0x6DFB] = "t", // 命名删除添
            [0x52A0] = "j", [0x79FB] = "y", [0x9664] = "c", [0x6E05] = "q", [0x7A7A] = "k", // 加移除清空
            [0x5168] = "q", [0x90E8] = "b", [0x5BFC] = "d", [0x5165] = "r", [0x5BFC] = "d", // 全部导入导
            [0x51FA] = "c", [0x626B] = "s", [0x63CF] = "m", [0x68C0] = "j", [0x67E5] = "c", // 出扫描检查
            [0x66F4] = "g", [0x65B0] = "x", [0x5173] = "g", [0x4E8E] = "y", [0x7248] = "b", // 更新关于版
            [0x672C] = "b", [0x4FE1] = "x", [0x606F] = "x", [0x8BBE] = "s", [0x7F6E] = "z", // 本信息设置
            [0x901A] = "t", [0x77E5] = "z", [0x786E] = "q", [0x8BA4] = "r", [0x53D6] = "q", // 知确认取
            [0x6D88] = "x", [0x5B8C] = "w", [0x6210] = "c", [0x5931] = "s", [0x8D25] = "b", // 消完成失败
            [0x6210] = "c", [0x529F] = "g", [0x9519] = "c", [0x8BEF] = "w", [0x8B66] = "j", // 功错误警
            [0x544A] = "g", [0x63D0] = "t", [0x793A] = "s", [0x901A] = "t", [0x77E5] = "z", // 告提示通知
            [0x53D1] = "f", [0x73B0] = "x", [0x5BFC] = "d", [0x5165] = "r", [0x5BFC] = "d", // 现导入导
            [0x51FA] = "c", [0x4FDD] = "b", [0x5B58] = "c", [0x52A0] = "j", [0x8F7D] = "z", // 出保存加载
            [0x91CD] = "z", [0x8F7D] = "z", [0x5237] = "s", [0x65B0] = "x", [0x540C] = "t", // 载刷新同
            [0x6B65] = "b", [0x6570] = "s", [0x636E] = "j", [0x8FDE] = "l", [0x63A5] = "j", // 步数据连接
            [0x7F51] = "w", [0x7EDC] = "l", [0x670D] = "f", [0x52A1] = "w", [0x5668] = "q", // 络服务器
            [0x5BA2] = "k", [0x6237] = "h", [0x7AEF] = "d", [0x672C] = "b", [0x5730] = "d", // 户端本地
            [0x4E91] = "y", [0x7AEF] = "d", [0x8FDC] = "y", [0x7A0B] = "c", [0x684C] = "z", // 端远程桌
            [0x9762] = "m", [0x8D44] = "z", [0x6E90] = "y", [0x7BA1] = "g", [0x7406] = "l", // 面资源管理
            [0x5668] = "q", [0x4EFB] = "r", [0x52A1] = "w", [0x7BA1] = "g", [0x7406] = "l", // 器任务管理
            [0x5668] = "q", [0x6CE8] = "z", [0x518C] = "c", [0x8868] = "b", [0x670D] = "f", // 器注册表服
            [0x52A1] = "w", [0x8FDB] = "j", [0x7A0B] = "c", [0x7F13] = "h", [0x5B58] = "c", // 务进程缓存
            [0x4E34] = "l", [0x65F6] = "s", [0x6587] = "w", [0x4EF6] = "j", [0x914D] = "p", // 时文件配
            [0x7F6E] = "z", [0x6570] = "s", [0x636E] = "j", [0x5E93] = "k", [0x8FDE] = "l", // 置数据库连
            [0x63A5] = "j", [0x5B57] = "z", [0x7B26] = "f", [0x4E32] = "c", [0x7F16] = "b", // 接字符串编
            [0x7801] = "m", [0x52A0] = "j", [0x5BC6] = "m", [0x89E3] = "j", [0x5BC6] = "m", // 码加密解密
            [0x538B] = "y", [0x7F29] = "s", [0x89E3] = "j", [0x538B] = "y", [0x6253] = "d", // 压缩解压打
            [0x5305] = "b", [0x5B89] = "a", [0x88C5] = "z", [0x5378] = "x", [0x8F7D] = "z", // 包安装卸载
            [0x542F] = "q", [0x52A8] = "d", [0x505C] = "t", [0x6B62] = "z", [0x91CD] = "z", // 动停止重
            [0x542F] = "q", [0x9000] = "t", [0x51FA] = "c", [0x767B] = "d", [0x5F55] = "l", // 启退出登录
            [0x6CE8] = "z", [0x518C] = "c", [0x5FD8] = "w", [0x8BB0] = "j", [0x5BC6] = "m", // 册忘记密
            [0x7801] = "m", [0x627E] = "z", [0x56DE] = "h", [0x4FEE] = "x", [0x6539] = "g", // 码找回修改
            [0x4E2A] = "g", [0x4EBA] = "r", [0x8D44] = "z", [0x6599] = "l", [0x5934] = "t", // 个人资料头
            [0x50CF] = "x", [0x6635] = "n", [0x79F0] = "c", [0x6027] = "x", [0x522B] = "b", // 像昵称性别
            [0x751F] = "s", [0x65E5] = "r", [0x5730] = "d", [0x533A] = "q", [0x57CE] = "c", // 日地区城
            [0x5E02] = "s", [0x7B80] = "j", [0x4ECB] = "j", [0x7B7E] = "q", [0x540D] = "m", // 市简介签名
            [0x7C89] = "f", [0x4E1D] = "s", [0x5173] = "g", [0x6CE8] = "z", [0x597D] = "h", // 丝关注好
            [0x53CB] = "y", [0x804A] = "l", [0x5929] = "t", [0x6D88] = "x", [0x606F] = "x", // 友聊天消息
            [0x901A] = "t", [0x77E5] = "z", [0x8BC4] = "p", [0x8BBA] = "l", [0x5206] = "f", // 知评论分
            [0x4EAB] = "x", [0x6536] = "s", [0x85CF] = "c", [0x70B9] = "d", [0x8D5E] = "z", // 享收藏点赞
            [0x4E3E] = "j", [0x62A5] = "b", [0x5C4F] = "p", [0x853D] = "b", [0x62C9] = "l", // 报屏蔽拉
            [0x9ED1] = "h", [0x4E3E] = "j", [0x62A5] = "b", [0x5220] = "s", [0x9664] = "c", // 黑举报删除
            [0x7F16] = "b", [0x8F91] = "j", [0x53D1] = "f", [0x5E03] = "b", [0x8349] = "c", // 辑发布草
            [0x7A3F] = "g", [0x9884] = "y", [0x89C8] = "l", [0x4FDD] = "b", [0x5B58] = "c", // 稿预览保存
            [0x53D1] = "f", [0x9001] = "s", [0x5B9A] = "d", [0x65F6] = "s", [0x5220] = "s", // 送定时删
            [0x9664] = "c", [0x7F6E] = "z", [0x9876] = "d", [0x7F6E] = "z", [0x5E95] = "d", // 除置顶置底
            [0x7F16] = "b", [0x8F91] = "j", [0x5386] = "l", [0x53F2] = "s", [0x7248] = "b", // 辑历史版
            [0x672C] = "b", [0x6BD4] = "b", [0x8F83] = "j", [0x5DEE] = "c", [0x5F02] = "y", // 本比较差异
            [0x5408] = "h", [0x5E76] = "b", [0x5206] = "f", [0x652F] = "z", [0x5F52] = "g", // 并分支归
            [0x5E76] = "b", [0x63D0] = "t", [0x4EA4] = "j", [0x62C9] = "l", [0x53D6] = "q", // 并提交拉取
            [0x63A8] = "t", [0x9001] = "s", [0x514B] = "k", [0x9686] = "l", [0x5206] = "f", // 送克隆分
            [0x652F] = "z", [0x5207] = "q", [0x6362] = "h", [0x5408] = "h", [0x5E76] = "b", // 支切换合并
            [0x51B2] = "c", [0x7A81] = "t", [0x89E3] = "j", [0x51B3] = "j", [0x56DE] = "h", // 突解决回
            [0x9000] = "t", [0x6062] = "h", [0x590D] = "f", [0x91CD] = "z", [0x7F6E] = "z", // 退恢复重置
            [0x64A4] = "c", [0x9500] = "x", [0x91CD] = "z", [0x505A] = "z", [0x5E94] = "y", // 销重做应
            [0x7528] = "y", [0x63D2] = "c", [0x4EF6] = "j", [0x6269] = "k", [0x5C55] = "z", // 用插件扩展
            [0x4E3B] = "z", [0x9898] = "t", [0x63D2] = "c", [0x4EF6] = "j", [0x5E02] = "s", // 题插件市
            [0x573A] = "c", [0x542F] = "q", [0x7528] = "y", [0x7981] = "j", [0x7528] = "y", // 场启用禁用
            [0x5378] = "x", [0x8F7D] = "z", [0x66F4] = "g", [0x65B0] = "x", [0x914D] = "p", // 载更新配
            [0x7F6E] = "z", [0x8BBE] = "s", [0x7F6E] = "z", [0x9996] = "s", [0x9009] = "x", // 置设置首选
            [0x9879] = "x", [0x901A] = "t", [0x7528] = "y", [0x9AD8] = "g", [0x7EA7] = "j", // 项通用高级
            [0x5B9E] = "s", [0x9A8C] = "y", [0x5BA4] = "s", [0x5F00] = "k", [0x53D1] = "f", // 验室开发
            [0x8005] = "z", [0x5DE5] = "g", [0x5177] = "j", [0x547D] = "m", [0x4EE4] = "l", // 者工具命令
            [0x9762] = "m", [0x677F] = "b", [0x63A7] = "k", [0x5236] = "z", [0x53F0] = "t", // 面板控制台
            [0x8C03] = "t", [0x8BD5] = "s", [0x7F51] = "w", [0x7EDC] = "l", [0x5B58] = "c", // 试网络存
            [0x50A8] = "c", [0x5E94] = "y", [0x7528] = "y", [0x670D] = "f", [0x52A1] = "w", // 储应用服务
            [0x5DE5] = "g", [0x4F5C] = "z", [0x8005] = "z", [0x5B89] = "a", [0x5168] = "q", // 作者安全
            [0x9690] = "y", [0x79C1] = "s", [0x8BB8] = "x", [0x53EF] = "k", [0x5185] = "n", // 私许可内
            [0x5BB9] = "r", [0x8BED] = "y", [0x8A00] = "y", [0x5730] = "d", [0x533A] = "q", // 容语言地区
            [0x65E5] = "r", [0x671F] = "q", [0x65F6] = "s", [0x95F4] = "j", [0x683C] = "g", // 期时间格
            [0x5F0F] = "s", [0x901A] = "t", [0x77E5] = "z", [0x6743] = "q", [0x9650] = "x", // 式通知权限
            [0x5B58] = "c", [0x50A8] = "c", [0x7F51] = "w", [0x7EDC] = "l", [0x84DD] = "l", // 储网络蓝
            [0x7259] = "y", [0x6253] = "d", [0x5370] = "y", [0x626B] = "s", [0x63CF] = "m", // 牙打印扫描
            [0x663E] = "x", [0x793A] = "s", [0x5206] = "f", [0x8FA8] = "b", [0x7387] = "l", // 示分辨率
            [0x591A] = "d", [0x5C4F] = "p", [0x5E55] = "m", [0x58C1] = "b", [0x7EB8] = "z", // 多屏幕壁纸
            [0x4E3B] = "z", [0x9898] = "t", [0x4EFB] = "r", [0x52A1] = "w", [0x680F] = "l", // 题任务栏
            [0x5F00] = "k", [0x59CB] = "s", [0x83DC] = "c", [0x5355] = "d", [0x901A] = "t", // 始菜单通
            [0x77E5] = "z", [0x533A] = "q", [0x64CD] = "c", [0x4F5C] = "z", [0x4E2D] = "z", // 知区操作中
            [0x5FC3] = "x", [0x65E5] = "r", [0x5386] = "l", [0x8BBE] = "s", [0x7F6E] = "z", // 心日历设置
            [0x641C] = "s", [0x7D22] = "s", [0x5E2E] = "b", [0x52A9] = "z", [0x53CD] = "f", // 索帮助反
            [0x9988] = "k", [0x5173] = "g", [0x4E8E] = "y", [0x9000] = "t", [0x51FA] = "c", // 馈关于退出
        };

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                if (_searchDebounceTimer == null)
                {
                    _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                    _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;
                }
                _searchDebounceTimer.Stop();
                _searchDebounceTimer.Start();
            }
        }

        private void SearchDebounceTimer_Tick(object? sender, object e)
        {
            _searchDebounceTimer?.Stop();
            ApplyFilters();
        }

        private void TagFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TagFilterComboBox == null) return;

            if (TagFilterComboBox.SelectedItem is string selectedTag)
            {
                _selectedTagFilter = selectedTag;
                ApplyFilters();
            }
        }

        private void CollectionFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CollectionFilterComboBox == null) return;
            if (CollectionFilterComboBox.SelectedItem is string selected)
            {
                _selectedCollectionFilter = selected;
                ApplyFilters();
            }
        }

        private async Task RefreshCollectionFilterAsync()
        {
            if (CollectionFilterComboBox == null) return;

            CollectionFilterComboBox.Items.Clear();
            CollectionFilterComboBox.Items.Add("全部游戏");

            try
            {
                var collections = await _gameService.GetAllCollectionsAsync();
                var counts = await _gameService.GetCollectionGameCountsAsync();
                foreach (var col in collections)
                {
                    var count = counts.TryGetValue(col.Id, out var c) ? c : 0;
                    CollectionFilterComboBox.Items.Add($"{col.Name} ({count})");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"刷新收藏夹筛选失败: {ex.Message}");
            }

            if (_selectedCollectionFilter != null)
            {
                var idx = -1;
                for (int i = 0; i < CollectionFilterComboBox.Items.Count; i++)
                {
                    if (CollectionFilterComboBox.Items[i] is string item &&
                        item.StartsWith(_selectedCollectionFilter.Split('(')[0].Trim()))
                    {
                        idx = i;
                        break;
                    }
                }
                CollectionFilterComboBox.SelectedIndex = idx >= 0 ? idx : 0;
            }
            else
            {
                CollectionFilterComboBox.SelectedIndex = 0;
            }
        }

        /// <summary>
        /// 获取文本的拼音首字母（仅支持词典中已收录的中文字符）
        /// </summary>
        private static string GetPinyinInitials(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var result = new System.Text.StringBuilder();
            foreach (char c in text)
            {
                if (_pinyinInitials.TryGetValue(c, out var pinyin))
                    result.Append(pinyin);
                else if (char.IsLetterOrDigit(c))
                    result.Append(char.ToLowerInvariant(c));
            }
            return result.ToString();
        }

        /// <summary>
        /// 计算搜索词与目标文本的匹配得分（0 = 不匹配，越高越相关）
        /// </summary>
        private static int CalculateMatchScore(string searchText, string targetText)
        {
            if (string.IsNullOrEmpty(targetText)) return 0;

            var target = targetText.ToLowerInvariant();
            var search = searchText.ToLowerInvariant();

            // Exact match gets highest score
            if (target == search) return 100;

            // Starts with search
            if (target.StartsWith(search)) return 80;

            // Contains search (exact substring)
            if (target.Contains(search)) return 60;

            // Character sequence match (all search chars appear in order in target)
            int searchIdx = 0;
            for (int i = 0; i < target.Length && searchIdx < search.Length; i++)
            {
                if (target[i] == search[searchIdx])
                    searchIdx++;
            }
            if (searchIdx == search.Length) return 40;

            // All search characters exist (any order)
            bool allCharsExist = search.All(c => target.Contains(c));
            if (allCharsExist) return 20;

            return 0;
        }

        private void ApplyFilters()
        {
            if (_filteredGames == null || _games == null) return;

            // Compute filter hash to detect filter changes
            var currentFilterHash = $"{SearchBox?.Text ?? ""}|{_selectedTagFilter ?? ""}|{_selectedCollectionFilter ?? ""}|{SortComboBox?.SelectedIndex ?? 0}";

            if (currentFilterHash != _lastFilterHash)
            {
                _currentPage = 1;
                _lastFilterHash = currentFilterHash;
            }

            if ((DateTime.UtcNow - _lastFileCacheRefresh).TotalMinutes > 5)
            {
                _fileExistsCache.Clear();
                _lastFileCacheRefresh = DateTime.UtcNow;
            }

            var settings = Models.UserSettings.Instance;
            var searchText = SearchBox?.Text?.Trim() ?? string.Empty;
            var hasSearch = !string.IsNullOrWhiteSpace(searchText);
            var searchLower = searchText.ToLowerInvariant();
            var hasTagFilter = _selectedTagFilter != null && _selectedTagFilter != "全部标签";
            var hasCollectionFilter = _selectedCollectionFilter != null && _selectedCollectionFilter != "全部游戏";
            var hideUnavailable = settings.HideUnavailableGames;

            var scoredGames = new List<(Game game, int score)>();

            foreach (var game in _games)
            {
                // Tag/collection filters first
                if (hasTagFilter && !game.Tags.Contains(_selectedTagFilter)) continue;
                if (hasCollectionFilter)
                {
                    var collectionName = _selectedCollectionFilter!.Split('(')[0].Trim();
                    if (!game.Collections.Any(c => c.Name == collectionName)) continue;
                }
                if (hideUnavailable && !IsGameExecutableAvailable(game)) continue;

                if (hasSearch)
                {
                    int score = 0;

                    // Check name
                    score = Math.Max(score, CalculateMatchScore(searchLower, game.Name));

                    // Check pinyin for name
                    var namePinyin = GetPinyinInitials(game.Name);
                    if (!string.IsNullOrEmpty(namePinyin))
                    {
                        score = Math.Max(score, CalculateMatchScore(searchLower, namePinyin) / 2);
                    }

                    // Check description
                    if (!string.IsNullOrEmpty(game.Description))
                        score = Math.Max(score, CalculateMatchScore(searchLower, game.Description) / 3);

                    // Check tags
                    foreach (var tag in game.Tags)
                    {
                        score = Math.Max(score, CalculateMatchScore(searchLower, tag) / 2);
                    }

                    if (score == 0) continue;
                    scoredGames.Add((game, score));
                }
                else
                {
                    scoredGames.Add((game, 0));
                }
            }

            var filtered = scoredGames
                .OrderByDescending(x => x.score)
                .ThenByDescending(x => x.game.CreatedAt)
                .Select(x => x.game)
                .ToList();

            // Then apply user-selected sorting
            filtered = SortGames(filtered).ToList();

            // Store total count before pagination
            _totalFilteredCount = filtered.Count;

            // Apply pagination
            var paged = filtered
                .Skip((_currentPage - 1) * _pageSize)
                .Take(_pageSize)
                .ToList();

            ApplyFilteredGamesDelta(paged);

            UpdateEmptyState();
            UpdatePaginationDisplay();
        }

        private void ApplyFilteredGamesDelta(List<Game> newFiltered)
        {
            var currentSet = new HashSet<Game>(_filteredGames);
            var newSet = new HashSet<Game>(newFiltered);

            for (int i = _filteredGames.Count - 1; i >= 0; i--)
            {
                if (!newSet.Contains(_filteredGames[i]))
                {
                    _filteredGames.RemoveAt(i);
                }
            }

            var currentList = _filteredGames.ToList();
            for (int i = 0; i < newFiltered.Count; i++)
            {
                if (i < currentList.Count && ReferenceEquals(currentList[i], newFiltered[i]))
                    continue;

                if (i < _filteredGames.Count)
                {
                    if (!ReferenceEquals(_filteredGames[i], newFiltered[i]))
                    {
                        var oldIndex = -1;
                        for (int j = i; j < _filteredGames.Count; j++)
                        {
                            if (ReferenceEquals(_filteredGames[j], newFiltered[i]))
                            {
                                oldIndex = j;
                                break;
                            }
                        }
                        if (oldIndex >= 0)
                        {
                            _filteredGames.Move(oldIndex, i);
                        }
                        else
                        {
                            _filteredGames.Insert(i, newFiltered[i]);
                        }
                    }
                }
                else
                {
                    _filteredGames.Add(newFiltered[i]);
                }
            }

            while (_filteredGames.Count > newFiltered.Count)
            {
                _filteredGames.RemoveAt(_filteredGames.Count - 1);
            }
        }

        private bool IsGameExecutableAvailable(Game game)
        {
            if (string.IsNullOrEmpty(game.ExecutablePath)) return false;
            if (_fileExistsCache.TryGetValue(game.ExecutablePath, out var cached))
                return cached;
            var exists = System.IO.File.Exists(game.ExecutablePath);
            _fileExistsCache[game.ExecutablePath] = exists;
            return exists;
        }

        private IEnumerable<Game> SortGames(IEnumerable<Game> games)
        {
            switch (_currentSortMode)
            {
                case "Name":
                    return games.OrderBy(g => g.Name);
                case "LaunchCount":
                    return games.OrderByDescending(g => g.LaunchCount);
                case "TotalPlayTime":
                    return games.OrderByDescending(g => g.TotalPlayTime);
                case "CreatedAt":
                    return games.OrderByDescending(g => g.CreatedAt);
                case "LastRunTime":
                    return games.OrderByDescending(g => g.LastRunTime ?? DateTime.MinValue);
                default:
                    return games.OrderByDescending(g => g.CreatedAt);
            }
        }

        private void UpdatePaginationDisplay()
        {
            RunOnUi(() =>
            {
                if (PageInfoText != null)
                {
                    PageInfoText.Text = $"第 {_currentPage} 页 / 共 {TotalPages} 页（共 {_totalFilteredCount} 个游戏）";
                }
                if (PrevPageButton != null)
                    PrevPageButton.IsEnabled = _currentPage > 1;
                if (NextPageButton != null)
                    NextPageButton.IsEnabled = _currentPage < TotalPages;
                if (PageSizeComboBox != null && PageSizeComboBox.SelectedItem == null)
                    PageSizeComboBox.SelectedIndex = 1; // Default to 30
            });
        }

        private void GoToPrevPage()
        {
            if (_currentPage > 1)
            {
                _currentPage--;
                ApplyFilters();
            }
        }

        private void GoToNextPage()
        {
            if (_currentPage < TotalPages)
            {
                _currentPage++;
                ApplyFilters();
            }
        }

        /// <summary>
        /// Force reset to page 1, e.g. when games are added/removed externally.
        /// </summary>
        public void ResetToFirstPage()
        {
            _currentPage = 1;
            _lastFilterHash = string.Empty; // Force re-apply
            ApplyFilters();
        }

        private void PageSizeChanged(int newSize)
        {
            _pageSize = newSize;
            _currentPage = 1;
            ApplyFilters();
        }
    }
}