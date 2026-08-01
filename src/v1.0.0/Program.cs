using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static class Program
{
    private const string ProgramVersion = "v1.0.0";
    private const int MaxEventBytes = 1024 * 1024;
    private const int SwRestore = 9;
    private const int VkEscape = 0x1B;
    private const int VkF8 = 0x77;
    private const int VkF9 = 0x78;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private static readonly CancellationTokenSource Stop = new CancellationTokenSource();
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = MaxEventBytes };
    private static readonly object LogLock = new object();
    private static readonly Queue<string> RecentMessageIds = new Queue<string>();
    private static readonly HashSet<string> RecentMessageIdSet = new HashSet<string>(StringComparer.Ordinal);
    private static DateTime _nextConnectionError = DateTime.MinValue;
    private static int _automationRunning;

    private sealed class Config
    {
        public string napcat_ws_url { get; set; }
        public string access_token { get; set; }
        public long group_id { get; set; }
        public long[] sender_qqs { get; set; }
        public string[] sender_names { get; set; }
        public string[] keywords { get; set; }
        public string keyword_match { get; set; }
        public bool ignore_case { get; set; }
        public string callback_url { get; set; }
        public string callback_bearer_token { get; set; }
        public int callback_timeout_ms { get; set; }
        public int reconnect_seconds { get; set; }
        public int dedupe_cache_size { get; set; }
        public bool automation_enabled { get; set; }
        public string game_process_name { get; set; }
        public int expected_client_width { get; set; }
        public int expected_client_height { get; set; }
        public int focus_wait_ms { get; set; }
        public ClickStep[] click_steps { get; set; }
    }

    private sealed class ClickStep
    {
        public string name { get; set; }
        public int x { get; set; }
        public int y { get; set; }
        public int delay_ms { get; set; }
        public int clicks { get; set; }
        public int interval_ms { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    private sealed class Hit
    {
        public long GroupId;
        public long UserId;
        public long MessageId;
        public long EventTime;
        public string SenderName;
        public string Content;
        public string Keyword;
    }

    private static int Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.Title = "QQ抓取 NapCat " + ProgramVersion;
        Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            Stop.Cancel();
        };

        if (args.Length > 0 && args[0] == "--self-test")
            return SelfTest();

        try
        {
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            Config config = LoadConfig(configPath);
            ValidateConfig(config);

            if (args.Length > 0 && args[0] == "--capture-points")
                return CapturePoints(config);
            if (args.Length > 0 && args[0] == "--dry-run-clicks")
                return DryRunClicks(config);
            if (args.Length > 0 && args[0] == "--preview-captured-points")
                return PreviewCapturedPoints(config);
            if (args.Length > 0 && args[0] == "--test-captured-clicks")
                return TestCapturedClicks(config);
            if (args.Length > 0 && args[0] == "--edit-captured-delays")
                return EditCapturedDelays();
            if (args.Length > 0 && args[0] == "--clear-captured-points")
                return ClearCapturedPoints();

            Console.WriteLine("QQ抓取 NapCat 版本：{0}", ProgramVersion);
            Console.WriteLine("NapCat地址：{0}", config.napcat_ws_url);
            Console.WriteLine("目标群号：{0}", config.group_id);
            Console.WriteLine("目标QQ号：{0}", String.Join(", ", Array.ConvertAll(config.sender_qqs, x => x.ToString())));
            Console.WriteLine("目标群名片：{0}", config.sender_names.Length == 0
                ? "未启用" : String.Join("、", config.sender_names));
            Console.WriteLine("关键词：{0}（{1}）", String.Join("、", config.keywords),
                config.keyword_match == "all" ? "必须全部出现" : "任意一个出现");
            Console.WriteLine("固定坐标自动点击：{0}", config.automation_enabled ? "已启用" : "未启用（安全状态）");
            Console.WriteLine("HTTP回调：{0}", String.IsNullOrWhiteSpace(config.callback_url) ? "未启用" : config.callback_url);
            Console.WriteLine("按 Ctrl+C 停止。\n");

            return Run(config).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("启动失败：{0}", ex.Message);
            return 1;
        }
    }

    private static Config LoadConfig(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("找不到 config.json。", path);
        return Json.Deserialize<Config>(File.ReadAllText(path, Encoding.UTF8));
    }

    private static void ValidateConfig(Config config)
    {
        if (config == null) throw new InvalidDataException("config.json 内容为空。");

        Uri wsUri;
        if (!Uri.TryCreate(config.napcat_ws_url, UriKind.Absolute, out wsUri) ||
            (wsUri.Scheme != "ws" && wsUri.Scheme != "wss"))
            throw new InvalidDataException("napcat_ws_url 必须是 ws:// 或 wss:// 地址。");
        if (config.group_id <= 0)
            throw new InvalidDataException("group_id 必须是有效群号。");
        if (config.sender_qqs == null) config.sender_qqs = new long[0];
        if (config.sender_names == null) config.sender_names = new string[0];
        if (config.sender_qqs.Length == 0 && config.sender_names.Length == 0)
            throw new InvalidDataException("sender_qqs 和 sender_names 至少需要填写一项。");
        if (config.keywords == null || config.keywords.Length == 0)
            throw new InvalidDataException("keywords 至少需要一个关键词。");
        for (int i = 0; i < config.sender_qqs.Length; i++)
            if (config.sender_qqs[i] <= 0) throw new InvalidDataException("sender_qqs 包含无效QQ号。");
        for (int i = 0; i < config.sender_names.Length; i++)
            if (String.IsNullOrWhiteSpace(config.sender_names[i])) throw new InvalidDataException("sender_names 不能包含空名称。");
        for (int i = 0; i < config.keywords.Length; i++)
            if (String.IsNullOrWhiteSpace(config.keywords[i])) throw new InvalidDataException("keywords 不能包含空关键词。");
        if (String.IsNullOrWhiteSpace(config.keyword_match)) config.keyword_match = "any";
        config.keyword_match = config.keyword_match.Trim().ToLowerInvariant();
        if (config.keyword_match != "any" && config.keyword_match != "all")
            throw new InvalidDataException("keyword_match 只能是 any 或 all。");

        if (!String.IsNullOrWhiteSpace(config.callback_url))
        {
            Uri callbackUri;
            if (!Uri.TryCreate(config.callback_url, UriKind.Absolute, out callbackUri) ||
                (callbackUri.Scheme != "http" && callbackUri.Scheme != "https"))
                throw new InvalidDataException("callback_url 必须为空或有效的 HTTP/HTTPS 地址。");
        }
        if (config.callback_timeout_ms < 200 || config.callback_timeout_ms > 30000)
            throw new InvalidDataException("callback_timeout_ms 必须在 200 到 30000 之间。");
        if (config.reconnect_seconds < 1 || config.reconnect_seconds > 60)
            throw new InvalidDataException("reconnect_seconds 必须在 1 到 60 之间。");
        if (config.dedupe_cache_size < 100 || config.dedupe_cache_size > 100000)
            throw new InvalidDataException("dedupe_cache_size 必须在 100 到 100000 之间。");

        if (String.IsNullOrWhiteSpace(config.game_process_name))
            config.game_process_name = "infinite_lagrange_cn";
        if (config.focus_wait_ms < 0 || config.focus_wait_ms > 5000)
            throw new InvalidDataException("focus_wait_ms 必须在 0 到 5000 之间。");
        if (config.click_steps == null) config.click_steps = new ClickStep[0];
        if (config.click_steps.Length > 50)
            throw new InvalidDataException("click_steps 最多允许 50 步。");
        for (int i = 0; i < config.click_steps.Length; i++)
        {
            ClickStep step = config.click_steps[i];
            if (step == null) throw new InvalidDataException("click_steps 不能包含空步骤。");
            if (String.IsNullOrWhiteSpace(step.name)) step.name = "步骤" + (i + 1);
            if (step.delay_ms < 0 || step.delay_ms > 60000)
                throw new InvalidDataException(step.name + " 的 delay_ms 必须在 0 到 60000 之间。");
            if (step.clicks < 1 || step.clicks > 100)
                throw new InvalidDataException(step.name + " 的 clicks 必须在 1 到 100 之间。");
            if (step.interval_ms < 0 || step.interval_ms > 10000)
                throw new InvalidDataException(step.name + " 的 interval_ms 必须在 0 到 10000 之间。");
            if (config.automation_enabled && (step.x < 0 || step.y < 0))
                throw new InvalidDataException(step.name + " 尚未配置有效坐标。");
        }
        if (config.automation_enabled)
        {
            if (config.click_steps.Length == 0)
                throw new InvalidDataException("启用自动点击前必须先配置 click_steps。");
            if (config.expected_client_width <= 0 || config.expected_client_height <= 0)
                throw new InvalidDataException("启用自动点击前必须填写采集到的游戏客户区宽高。");
        }
    }

    private static async Task<int> Run(Config config)
    {
        while (!Stop.IsCancellationRequested)
        {
            using (ClientWebSocket socket = new ClientWebSocket())
            {
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                if (!String.IsNullOrWhiteSpace(config.access_token))
                    socket.Options.SetRequestHeader("Authorization", "Bearer " + config.access_token.Trim());

                try
                {
                    Log("正在连接 NapCat WebSocket：" + config.napcat_ws_url);
                    await socket.ConnectAsync(new Uri(config.napcat_ws_url), Stop.Token);
                    _nextConnectionError = DateTime.MinValue;
                    Log("已连接 NapCat，开始接收群消息事件。");
                    await ReceiveLoop(socket, config);
                    if (!Stop.IsCancellationRequested)
                        Log("NapCat 连接已关闭，准备重新连接。");
                }
                catch (OperationCanceledException)
                {
                    if (!Stop.IsCancellationRequested) throw;
                }
                catch (Exception ex)
                {
                    LogConnectionError(ex.Message);
                }
            }

            if (!Stop.IsCancellationRequested)
            {
                try { await Task.Delay(config.reconnect_seconds * 1000, Stop.Token); }
                catch (OperationCanceledException) { }
            }
        }

        Log("监控已停止。");
        return 0;
    }

    private static async Task ReceiveLoop(ClientWebSocket socket, Config config)
    {
        byte[] buffer = new byte[64 * 1024];
        while (!Stop.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using (MemoryStream message = new MemoryStream())
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), Stop.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxEventBytes)
                        throw new InvalidDataException("收到的单条事件超过 1MB，已拒绝处理。");
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string eventJson = Encoding.UTF8.GetString(message.ToArray());
                    ProcessEvent(config, eventJson);
                }
            }
        }
    }

    private static void ProcessEvent(Config config, string eventJson)
    {
        try
        {
            Hit hit;
            if (!TryMatch(config, eventJson, out hit))
                return;
            if (IsDuplicate(hit, config.dedupe_cache_size))
                return;

            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["detected_at"] = DateTimeOffset.Now.ToString("o");
            payload["event_time"] = hit.EventTime;
            payload["group_id"] = hit.GroupId;
            payload["user_id"] = hit.UserId;
            payload["message_id"] = hit.MessageId;
            payload["sender_name"] = hit.SenderName;
            payload["keyword"] = hit.Keyword;
            payload["content"] = hit.Content;

            string json = Json.Serialize(payload);
            AppendLine(HitLogPath(), json);
            Log(String.Format("命中：群={0} QQ={1} 关键词={2} 内容={3}",
                hit.GroupId, hit.UserId, hit.Keyword, hit.Content));

            QueueAutomation(config, hit);
            if (!String.IsNullOrWhiteSpace(config.callback_url))
                QueueCallback(config, json);
        }
        catch (Exception ex)
        {
            Log("处理事件失败：" + ex.Message);
        }
    }

    private static bool TryMatch(Config config, string eventJson, out Hit hit)
    {
        hit = null;
        Dictionary<string, object> data = Json.DeserializeObject(eventJson) as Dictionary<string, object>;
        if (data == null || GetString(data, "post_type") != "message" ||
            GetString(data, "message_type") != "group")
            return false;

        long groupId = GetLong(data, "group_id");
        long userId = GetLong(data, "user_id");
        if (groupId != config.group_id)
            return false;

        Dictionary<string, object> sender = GetDictionary(data, "sender");
        string senderName = GetString(sender, "card");
        string nickname = GetString(sender, "nickname");
        if (String.IsNullOrWhiteSpace(senderName)) senderName = nickname;
        if (!IsTargetSender(config, userId, senderName, nickname))
            return false;

        string content = GetString(data, "raw_message");
        if (String.IsNullOrEmpty(content))
            content = ExtractMessage(data);

        StringComparison comparison = config.ignore_case
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        List<string> matchedKeywords = new List<string>();
        for (int i = 0; i < config.keywords.Length; i++)
        {
            if (content.IndexOf(config.keywords[i], comparison) >= 0)
                matchedKeywords.Add(config.keywords[i]);
            else if (config.keyword_match == "all")
                return false;
        }
        if (matchedKeywords.Count == 0)
            return false;

        hit = new Hit
        {
            GroupId = groupId,
            UserId = userId,
            MessageId = GetLong(data, "message_id"),
            EventTime = GetLong(data, "time"),
            SenderName = senderName,
            Content = content,
            Keyword = String.Join("+", matchedKeywords.ToArray())
        };
        return true;
    }

    private static bool IsTargetSender(Config config, long userId, string card, string nickname)
    {
        if (Array.IndexOf(config.sender_qqs, userId) >= 0)
            return true;
        for (int i = 0; i < config.sender_names.Length; i++)
        {
            string expected = config.sender_names[i].Trim();
            if (String.Equals((card ?? "").Trim(), expected, StringComparison.OrdinalIgnoreCase) ||
                String.Equals((nickname ?? "").Trim(), expected, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string ExtractMessage(Dictionary<string, object> data)
    {
        object value;
        if (!data.TryGetValue("message", out value) || value == null)
            return "";

        string text = value as string;
        if (text != null) return text;

        IEnumerable segments = value as IEnumerable;
        if (segments == null) return "";
        StringBuilder result = new StringBuilder();
        foreach (object item in segments)
        {
            Dictionary<string, object> segment = item as Dictionary<string, object>;
            if (segment == null || GetString(segment, "type") != "text") continue;
            Dictionary<string, object> segmentData = GetDictionary(segment, "data");
            result.Append(GetString(segmentData, "text"));
        }
        return result.ToString();
    }

    private static bool IsDuplicate(Hit hit, int cacheSize)
    {
        if (hit.MessageId == 0) return false;
        string key = hit.GroupId + ":" + hit.UserId + ":" + hit.MessageId;
        if (!RecentMessageIdSet.Add(key)) return true;
        RecentMessageIds.Enqueue(key);
        while (RecentMessageIds.Count > cacheSize)
            RecentMessageIdSet.Remove(RecentMessageIds.Dequeue());
        return false;
    }

    private static void QueueAutomation(Config config, Hit hit)
    {
        if (!config.automation_enabled)
        {
            Log("固定坐标自动点击未启用，本次仅记录命中。");
            return;
        }
        if (Interlocked.CompareExchange(ref _automationRunning, 1, 0) != 0)
        {
            Log("上一条自动点击序列尚未结束，本次命中已跳过。");
            return;
        }

        ThreadPool.QueueUserWorkItem(delegate
        {
            try { ExecuteClicks(config, hit); }
            catch (Exception ex) { Log("自动点击失败：" + ex.Message); }
            finally { Interlocked.Exchange(ref _automationRunning, 0); }
        });
    }

    private static bool ExecuteClicks(Config config, Hit hit)
    {
        IntPtr window = FindGameWindow(config.game_process_name);
        if (window == IntPtr.Zero)
        {
            Log("未找到游戏窗口，已取消自动点击：" + config.game_process_name);
            return false;
        }

        NativeRect client;
        if (!GetClientRect(window, out client) || !ClientSizeMatches(config, client))
            return false;

        if (!ActivateWindow(window, config.focus_wait_ms))
        {
            Log("游戏窗口未成功置于前台，已取消自动点击以防误点。");
            return false;
        }

        NativePoint original;
        bool restoreCursor = GetCursorPos(out original);
        try
        {
            NativePoint origin = new NativePoint { X = 0, Y = 0 };
            if (!ClientToScreen(window, ref origin) || !SetCursorPos(origin.X, origin.Y))
            {
                Log("无法把鼠标归零到游戏客户区左上角，已取消自动点击。");
                return false;
            }
            Log("鼠标已归零：游戏客户区左上角为 (0,0)。");
            if (!WaitCancelable(300)) return false;

            Log("开始固定坐标点击，来源消息：" + hit.Content);
            for (int i = 0; i < config.click_steps.Length; i++)
            {
                ClickStep step = config.click_steps[i];
                if (!WaitCancelable(step.delay_ms))
                {
                    Log("自动点击已由 Ctrl+C 或 Esc 中止。");
                    return false;
                }
                if (GetForegroundWindow() != window)
                {
                    Log("游戏已失去前台，已中止自动点击以防误点。");
                    return false;
                }
                if (!GetClientRect(window, out client) || !ClientSizeMatches(config, client))
                    return false;
                if (step.x < 0 || step.y < 0 || step.x >= client.Right || step.y >= client.Bottom)
                {
                    Log(step.name + " 的坐标超出游戏客户区，已中止自动点击。");
                    return false;
                }

                NativePoint point = new NativePoint { X = step.x, Y = step.y };
                if (!ClientToScreen(window, ref point) || !SetCursorPos(point.X, point.Y))
                {
                    Log(step.name + " 无法定位鼠标，已中止自动点击。");
                    return false;
                }

                for (int click = 0; click < step.clicks; click++)
                {
                    if (GetForegroundWindow() != window || IsKeyDown(VkEscape))
                    {
                        Log("游戏失去前台或检测到 Esc，已中止自动点击。");
                        return false;
                    }
                    mouse_event(MouseLeftDown, 0, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(30);
                    mouse_event(MouseLeftUp, 0, 0, 0, UIntPtr.Zero);
                    if (click + 1 < step.clicks && !WaitCancelable(step.interval_ms))
                        return false;
                }
                Log(String.Format("已执行：{0}，坐标=({1},{2})，点击={3}次",
                    step.name, step.x, step.y, step.clicks));
            }
            Log("固定坐标点击序列执行完成。");
            return true;
        }
        finally
        {
            if (restoreCursor) SetCursorPos(original.X, original.Y);
        }
    }

    private static bool ClientSizeMatches(Config config, NativeRect client)
    {
        int width = client.Right - client.Left;
        int height = client.Bottom - client.Top;
        if (width == config.expected_client_width && height == config.expected_client_height)
            return true;
        Log(String.Format("游戏客户区尺寸为 {0}x{1}，配置要求 {2}x{3}，已取消自动点击以防坐标偏移。",
            width, height, config.expected_client_width, config.expected_client_height));
        return false;
    }

    private static bool ActivateWindow(IntPtr window, int waitMs)
    {
        ShowWindow(window, SwRestore);
        uint currentThread = GetCurrentThreadId();
        IntPtr foreground = GetForegroundWindow();
        uint foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, IntPtr.Zero);
        uint targetThread = GetWindowThreadProcessId(window, IntPtr.Zero);
        bool attachedForeground = false;
        bool attachedTarget = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
                attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);
            if (targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread)
                attachedTarget = AttachThreadInput(currentThread, targetThread, true);
            BringWindowToTop(window);
            SetForegroundWindow(window);
            SetActiveWindow(window);
            SetFocus(window);
        }
        finally
        {
            if (attachedTarget) AttachThreadInput(currentThread, targetThread, false);
            if (attachedForeground) AttachThreadInput(currentThread, foregroundThread, false);
        }
        return WaitCancelable(waitMs) && GetForegroundWindow() == window;
    }

    private static bool WaitCancelable(int milliseconds)
    {
        int waited = 0;
        while (waited < milliseconds)
        {
            if (Stop.IsCancellationRequested || IsKeyDown(VkEscape)) return false;
            int slice = Math.Min(50, milliseconds - waited);
            Thread.Sleep(slice);
            waited += slice;
        }
        return !Stop.IsCancellationRequested && !IsKeyDown(VkEscape);
    }

    private static IntPtr FindGameWindow(string processName)
    {
        string name = Path.GetFileNameWithoutExtension((processName ?? "").Trim());
        if (name.Length == 0) return IntPtr.Zero;
        Process[] processes = Process.GetProcessesByName(name);
        IntPtr result = IntPtr.Zero;
        for (int i = 0; i < processes.Length; i++)
        {
            try
            {
                IntPtr window = processes[i].MainWindowHandle;
                if (result == IntPtr.Zero && window != IntPtr.Zero) result = window;
            }
            catch { }
            finally { processes[i].Dispose(); }
        }
        return result;
    }

    private static int CapturePoints(Config config)
    {
        IntPtr window = FindGameWindow(config.game_process_name);
        if (window == IntPtr.Zero)
        {
            Console.Error.WriteLine("未找到游戏窗口：{0}", config.game_process_name);
            return 1;
        }
        NativeRect client;
        if (!GetClientRect(window, out client))
        {
            Console.Error.WriteLine("无法读取游戏客户区。");
            return 1;
        }
        int width = client.Right - client.Left;
        int height = client.Bottom - client.Top;
        List<ClickStep> steps = new List<ClickStep>();
        string path = CapturedPointsPath();
        if (File.Exists(path))
        {
            Config previous = Json.Deserialize<Config>(File.ReadAllText(path, Encoding.UTF8));
            try
            {
                ValidateCapturedPoints(previous, width, height);
                if (!String.Equals(previous.game_process_name, config.game_process_name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("已有坐标文件的游戏进程名与当前配置不同。");
                steps.AddRange(previous.click_steps);
                Console.WriteLine("已载入现有 {0} 步，新坐标将从步骤{1}继续追加。", steps.Count, steps.Count + 1);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("无法续采：{0}", ex.Message);
                return 1;
            }
        }
        Console.WriteLine("游戏客户区：{0}x{1}", width, height);
        Console.WriteLine("按实际购买顺序操作：鼠标移到位置后按 F8 记录；按 F9 完成；按 Esc 取消。");
        while (IsKeyDown(VkF8) || IsKeyDown(VkF9)) Thread.Sleep(30);

        while (!Stop.IsCancellationRequested)
        {
            if (IsKeyDown(VkEscape))
            {
                Console.WriteLine("已取消，未写入坐标文件。");
                return 1;
            }
            if (IsKeyDown(VkF9)) break;
            if (IsKeyDown(VkF8))
            {
                NativeRect currentClient;
                if (!GetClientRect(window, out currentClient) ||
                    currentClient.Right - currentClient.Left != width ||
                    currentClient.Bottom - currentClient.Top != height)
                {
                    Console.Error.WriteLine("采集期间游戏客户区尺寸发生变化，已取消本次采集。");
                    return 1;
                }
                NativePoint point;
                if (GetCursorPos(out point) && ScreenToClient(window, ref point) &&
                    point.X >= 0 && point.Y >= 0 && point.X < width && point.Y < height)
                {
                    ClickStep step = new ClickStep
                    {
                        name = "步骤" + (steps.Count + 1),
                        x = point.X,
                        y = point.Y,
                        delay_ms = 0,
                        clicks = 1,
                        interval_ms = 50
                    };
                    Console.WriteLine("已记录 {0}：({1},{2})", step.name, step.x, step.y);
                    while (IsKeyDown(VkF8)) Thread.Sleep(30);
                    Console.WriteLine("请切回本窗口，填写该步点击前等待时间。");
                    step.delay_ms = ReadDelayMs(step.name, steps.Count == 0 ? 0 : 1000);
                    steps.Add(step);
                }
                else
                    Console.WriteLine("鼠标不在游戏客户区内，本次未记录。");
                while (IsKeyDown(VkF8)) Thread.Sleep(30);
            }
            Thread.Sleep(20);
        }

        if (steps.Count == 0)
        {
            Console.Error.WriteLine("没有记录任何位置。");
            return 1;
        }
        SaveCapturedPoints(config.game_process_name, width, height, steps.ToArray());
        Console.WriteLine("已保存：{0}", path);
        return 0;
    }

    private static int PreviewCapturedPoints(Config config)
    {
        string path = CapturedPointsPath();
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("找不到点击位置待确认.json。");
            return 1;
        }
        Config captured = Json.Deserialize<Config>(File.ReadAllText(path, Encoding.UTF8));
        if (captured == null || !String.Equals(captured.game_process_name, config.game_process_name, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("坐标文件的游戏进程名与当前配置不同。");
            return 1;
        }
        IntPtr window = FindGameWindow(config.game_process_name);
        if (window == IntPtr.Zero)
        {
            Console.Error.WriteLine("未找到游戏窗口：{0}", config.game_process_name);
            return 1;
        }
        NativeRect client;
        if (!GetClientRect(window, out client))
        {
            Console.Error.WriteLine("无法读取游戏客户区。");
            return 1;
        }
        try { ValidateCapturedPoints(captured, client.Right - client.Left, client.Bottom - client.Top); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("坐标预览已拒绝：{0}", ex.Message);
            return 1;
        }

        if (!ActivateWindow(window, config.focus_wait_ms))
        {
            Console.Error.WriteLine("游戏窗口未成功置于前台，坐标预览已取消。");
            return 1;
        }

        NativePoint original;
        bool restoreCursor = GetCursorPos(out original);
        Console.WriteLine("开始预览 {0} 个坐标：只移动鼠标，绝不点击；按 Esc 中止。", captured.click_steps.Length);
        try
        {
            for (int i = 0; i < captured.click_steps.Length; i++)
            {
                if (GetForegroundWindow() != window)
                {
                    Console.Error.WriteLine("游戏失去前台，预览已中止。");
                    return 1;
                }
                ClickStep step = captured.click_steps[i];
                NativePoint point = new NativePoint { X = step.x, Y = step.y };
                if (!ClientToScreen(window, ref point) || !SetCursorPos(point.X, point.Y))
                {
                    Console.Error.WriteLine("无法定位 {0}。", step.name);
                    return 1;
                }
                Console.WriteLine("预览 {0}：客户区坐标 ({1},{2})", step.name, step.x, step.y);
                if (!WaitCancelable(900))
                {
                    Console.WriteLine("坐标预览已中止。");
                    return 1;
                }
            }
            Console.WriteLine("坐标预览完成：共 {0} 步，未执行任何点击。", captured.click_steps.Length);
            return 0;
        }
        finally
        {
            if (restoreCursor) SetCursorPos(original.X, original.Y);
        }
    }

    private static int TestCapturedClicks(Config config)
    {
        string path = CapturedPointsPath();
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("找不到点击位置待确认.json。");
            return 1;
        }
        Config captured = Json.Deserialize<Config>(File.ReadAllText(path, Encoding.UTF8));
        if (captured == null || !String.Equals(captured.game_process_name, config.game_process_name, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("坐标文件的游戏进程名与当前配置不同。");
            return 1;
        }
        IntPtr window = FindGameWindow(config.game_process_name);
        NativeRect client;
        if (window == IntPtr.Zero || !GetClientRect(window, out client))
        {
            Console.Error.WriteLine("未找到可用的游戏窗口：{0}", config.game_process_name);
            return 1;
        }
        try { ValidateCapturedPoints(captured, client.Right - client.Left, client.Bottom - client.Top); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("真实点击测试已拒绝：{0}", ex.Message);
            return 1;
        }

        ClickStep[] testSteps = new ClickStep[captured.click_steps.Length];
        for (int i = 0; i < captured.click_steps.Length; i++)
        {
            ClickStep source = captured.click_steps[i];
            testSteps[i] = new ClickStep
            {
                name = source.name,
                x = source.x,
                y = source.y,
                delay_ms = source.delay_ms,
                clicks = source.clicks,
                interval_ms = source.interval_ms
            };
        }
        config.expected_client_width = captured.expected_client_width;
        config.expected_client_height = captured.expected_client_height;
        config.click_steps = testSteps;

        Console.WriteLine("警告：本次会执行 {0} 个真实左键点击。3秒内按 Esc 可取消。", testSteps.Length);
        for (int seconds = 3; seconds > 0; seconds--)
        {
            Console.WriteLine(seconds);
            if (!WaitCancelable(1000))
            {
                Console.WriteLine("真实点击测试已取消。");
                return 1;
            }
        }
        return ExecuteClicks(config, new Hit { Content = "手动真实点击测试" }) ? 0 : 1;
    }

    private static int EditCapturedDelays()
    {
        string path = CapturedPointsPath();
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("找不到点击位置待确认.json。");
            return 1;
        }
        Config captured = Json.Deserialize<Config>(File.ReadAllText(path, Encoding.UTF8));
        if (captured == null)
        {
            Console.Error.WriteLine("无法编辑等待时间：坐标文件内容为空。");
            return 1;
        }
        try { ValidateCapturedPoints(captured, captured.expected_client_width, captured.expected_client_height); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("无法编辑等待时间：{0}", ex.Message);
            return 1;
        }

        Console.WriteLine("填写每一步点击前的等待时间，单位为毫秒；直接回车保留当前值。");
        for (int i = 0; i < captured.click_steps.Length; i++)
        {
            ClickStep step = captured.click_steps[i];
            step.delay_ms = ReadDelayMs(String.Format("{0} 坐标({1},{2})", step.name, step.x, step.y), step.delay_ms);
        }
        SaveCapturedPoints(captured.game_process_name, captured.expected_client_width,
            captured.expected_client_height, captured.click_steps);
        Console.WriteLine("等待时间已保存：{0}", path);
        return 0;
    }

    private static int ClearCapturedPoints()
    {
        string path = CapturedPointsPath();
        if (!File.Exists(path))
        {
            Console.WriteLine("当前没有已采集坐标，无需清除。");
            return 0;
        }
        Console.WriteLine("将清除当前全部点击位置；下次采集会从步骤1重新开始。");
        Console.Write("输入 CLEAR 确认，其他输入取消：");
        if (!String.Equals(Console.ReadLine(), "CLEAR", StringComparison.Ordinal))
        {
            Console.WriteLine("已取消，坐标文件未改变。");
            return 0;
        }

        string backupDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "已清除记录");
        Directory.CreateDirectory(backupDirectory);
        string backup = Path.Combine(backupDirectory,
            "点击位置-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".json");
        File.Move(path, backup);
        Console.WriteLine("当前坐标已清除，备份保存在：{0}", backup);
        return 0;
    }

    private static int ReadDelayMs(string label, int currentValue)
    {
        while (true)
        {
            Console.Write("{0} 等待毫秒数（当前/默认 {1}，范围0-60000）：", label, currentValue);
            string input = Console.ReadLine();
            if (String.IsNullOrWhiteSpace(input)) return currentValue;
            int value;
            if (Int32.TryParse(input.Trim(), out value) && value >= 0 && value <= 60000)
                return value;
            Console.WriteLine("输入无效，请填写 0 到 60000 的整数。");
        }
    }

    private static void SaveCapturedPoints(string processName, int width, int height, ClickStep[] steps)
    {
        Dictionary<string, object> result = new Dictionary<string, object>();
        result["game_process_name"] = processName;
        result["expected_client_width"] = width;
        result["expected_client_height"] = height;
        result["click_steps"] = steps;
        File.WriteAllText(CapturedPointsPath(), Json.Serialize(result), new UTF8Encoding(false));
    }

    private static void ValidateCapturedPoints(Config captured, int width, int height)
    {
        if (captured == null || captured.click_steps == null || captured.click_steps.Length == 0)
            throw new InvalidDataException("坐标文件没有点击步骤。");
        if (captured.expected_client_width != width || captured.expected_client_height != height)
            throw new InvalidDataException(String.Format("游戏客户区为 {0}x{1}，坐标文件要求 {2}x{3}。",
                width, height, captured.expected_client_width, captured.expected_client_height));
        for (int i = 0; i < captured.click_steps.Length; i++)
        {
            ClickStep step = captured.click_steps[i];
            if (step == null || step.x < 0 || step.y < 0 || step.x >= width || step.y >= height)
                throw new InvalidDataException("步骤" + (i + 1) + " 的坐标超出游戏客户区。");
            if (step.delay_ms < 0 || step.delay_ms > 60000)
                throw new InvalidDataException("步骤" + (i + 1) + " 的等待时间必须在 0 到 60000 毫秒之间。");
            if (step.clicks < 1 || step.clicks > 100 || step.interval_ms < 0 || step.interval_ms > 10000)
                throw new InvalidDataException("步骤" + (i + 1) + " 的点击次数或间隔无效。");
        }
    }

    private static string CapturedPointsPath()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "点击位置待确认.json");
    }

    private static int DryRunClicks(Config config)
    {
        Console.WriteLine("固定坐标点击演练（不会移动鼠标、不会点击）");
        Console.WriteLine("启用状态：{0}", config.automation_enabled ? "已启用" : "未启用");
        Console.WriteLine("游戏进程：{0}", config.game_process_name);
        Console.WriteLine("客户区：{0}x{1}", config.expected_client_width, config.expected_client_height);
        if (config.click_steps.Length == 0)
        {
            Console.WriteLine("尚未配置点击步骤，请先运行“采集点击位置.bat”。");
            return 0;
        }
        for (int i = 0; i < config.click_steps.Length; i++)
        {
            ClickStep step = config.click_steps[i];
            Console.WriteLine("{0}. {1} 坐标=({2},{3}) 延时={4}ms 点击={5}次 间隔={6}ms",
                i + 1, step.name, step.x, step.y, step.delay_ms, step.clicks, step.interval_ms);
        }
        return 0;
    }

    private static bool IsKeyDown(int key)
    {
        return (GetAsyncKeyState(key) & 0x8000) != 0;
    }

    private static void QueueCallback(Config config, string json)
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                byte[] body = Encoding.UTF8.GetBytes(json);
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(config.callback_url);
                request.Method = "POST";
                request.ContentType = "application/json; charset=utf-8";
                request.ContentLength = body.Length;
                request.Timeout = config.callback_timeout_ms;
                request.ReadWriteTimeout = config.callback_timeout_ms;
                if (!String.IsNullOrWhiteSpace(config.callback_bearer_token))
                    request.Headers[HttpRequestHeader.Authorization] =
                        "Bearer " + config.callback_bearer_token.Trim();
                using (Stream stream = request.GetRequestStream())
                    stream.Write(body, 0, body.Length);
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    Log("HTTP 回调成功：" + (int)response.StatusCode);
            }
            catch (Exception ex)
            {
                Log("HTTP 回调失败：" + ex.Message);
            }
        });
    }

    private static Dictionary<string, object> GetDictionary(Dictionary<string, object> data, string key)
    {
        object value;
        return data != null && data.TryGetValue(key, out value)
            ? value as Dictionary<string, object>
            : null;
    }

    private static string GetString(Dictionary<string, object> data, string key)
    {
        object value;
        return data != null && data.TryGetValue(key, out value) && value != null
            ? Convert.ToString(value)
            : "";
    }

    private static long GetLong(Dictionary<string, object> data, string key)
    {
        object value;
        if (data == null || !data.TryGetValue(key, out value) || value == null) return 0;
        try { return Convert.ToInt64(value); }
        catch { return 0; }
    }

    private static void LogConnectionError(string message)
    {
        if (DateTime.UtcNow < _nextConnectionError) return;
        Log("连接 NapCat 失败：" + message + "；将自动重试。请确认 NapCat WebSocket 已启用。");
        _nextConnectionError = DateTime.UtcNow.AddSeconds(10);
    }

    private static void Log(string message)
    {
        string line = String.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}", DateTime.Now, message);
        lock (LogLock)
        {
            Console.WriteLine(line);
            try { AppendLine(RunLogPath(), line); }
            catch { }
        }
    }

    private static void AppendLine(string path, string line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
    }

    private static string RunLogPath()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "日志", "运行日志-" + ProgramVersion + ".txt");
    }

    private static string HitLogPath()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "日志", "命中记录-" + ProgramVersion + ".jsonl");
    }

    private static int SelfTest()
    {
        Config config = new Config
        {
            napcat_ws_url = "ws://127.0.0.1:3001",
            access_token = "",
            group_id = 152636120,
            sender_qqs = new long[] { 106589552 },
            sender_names = new string[] { "请确保自己知晓公告所有内容" },
            keywords = new string[] { "认证", "上架" },
            keyword_match = "all",
            ignore_case = true,
            callback_url = "",
            callback_bearer_token = "",
            callback_timeout_ms = 1500,
            reconnect_seconds = 1,
            dedupe_cache_size = 2000,
            automation_enabled = false,
            game_process_name = "infinite_lagrange_cn",
            expected_client_width = 0,
            expected_client_height = 0,
            focus_wait_ms = 150,
            click_steps = new ClickStep[0]
        };

        string sample = "{\"time\":100,\"post_type\":\"message\",\"message_type\":\"group\"," +
            "\"group_id\":152636120,\"user_id\":106589552,\"message_id\":9001," +
            "\"raw_message\":\"【星空巡游者认证】上架，数量0\"," +
            "\"sender\":{\"nickname\":\"甲\",\"card\":\"请确保自己知晓公告所有内容\"}}";
        Hit hit;
        if (!TryMatch(config, sample, out hit) || hit.UserId != 106589552 || hit.Keyword != "认证+上架") return 1;
        if (!TryMatch(config, sample.Replace("106589552", "106589553"), out hit)) return 2;
        string wrongSender = sample.Replace("106589552", "106589553")
            .Replace("请确保自己知晓公告所有内容", "其他人");
        if (TryMatch(config, wrongSender, out hit)) return 3;
        if (TryMatch(config, sample.Replace("152636120", "152636121"), out hit)) return 4;
        if (TryMatch(config, sample.Replace("认证", "凭证"), out hit)) return 5;

        config.keyword_match = "any";
        if (!TryMatch(config, sample.Replace("认证", "凭证"), out hit) || hit.Keyword != "上架") return 6;
        config.keyword_match = "all";

        string segmented = "{\"time\":101,\"post_type\":\"message\",\"message_type\":\"group\"," +
            "\"group_id\":152636120,\"user_id\":106589552,\"message_id\":9002," +
            "\"message\":[{\"type\":\"text\",\"data\":{\"text\":\"认证现已上架\"}}]}";
        if (!TryMatch(config, segmented, out hit) || hit.Content != "认证现已上架") return 7;
        if (IsDuplicate(hit, config.dedupe_cache_size)) return 8;
        if (!IsDuplicate(hit, config.dedupe_cache_size)) return 9;

        ValidateConfig(config);
        config.automation_enabled = true;
        bool unsafeConfigRejected = false;
        try { ValidateConfig(config); }
        catch (InvalidDataException) { unsafeConfigRejected = true; }
        if (!unsafeConfigRejected) return 10;

        Config captured = new Config
        {
            expected_client_width = 100,
            expected_client_height = 100,
            click_steps = new ClickStep[]
            {
                new ClickStep { name = "测试", x = 99, y = 99, clicks = 1 }
            }
        };
        ValidateCapturedPoints(captured, 100, 100);
        captured.click_steps[0].x = 100;
        bool badPointRejected = false;
        try { ValidateCapturedPoints(captured, 100, 100); }
        catch (InvalidDataException) { badPointRejected = true; }
        if (!badPointRejected) return 11;
        captured.click_steps[0].x = 99;
        captured.click_steps[0].delay_ms = 60001;
        bool badDelayRejected = false;
        try { ValidateCapturedPoints(captured, 100, 100); }
        catch (InvalidDataException) { badDelayRejected = true; }
        if (!badDelayRejected) return 12;
        Console.WriteLine("SELF-TEST OK");
        return 0;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachState);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
