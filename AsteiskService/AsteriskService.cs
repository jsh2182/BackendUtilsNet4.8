using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Contexts;
using AsterNET.Manager;
using AsterNET.Manager.Action;
using AsterNET.Manager.Event;
using AsterNET.Manager.Response;
using static System.Net.Mime.MediaTypeNames;

namespace BackendUtils.AsteriskListenerLib
{
    public class AsteriskService
    {
        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "asterisk_logs.txt");
        private ManagerConnection _manager;

        // داده‌های کمکی برای مدیریت رویداد NewStateEvent که قبل از NewExtenEvent می‌آیند (اگر لازم بود)
        private readonly Dictionary<string, NewStateEvent> _earlyStateEvents = new Dictionary<string, NewStateEvent>();
        private readonly Dictionary<string, DateTime> _earlyStateEventTimes = new Dictionary<string, DateTime>();

        // حداکثر مدت زمانی که نگهداری می‌کنیم (برای رویدادهای زودهنگام NewState)
        private readonly TimeSpan _earlyEventTimeout = TimeSpan.FromSeconds(5);
        private readonly HashSet<string> _internalNumbers;// شماره های داخلی برای تشخیص اینکه تماس ورودی است یا خروجی
        private readonly Action<string, string, bool> _callHandler;
        private readonly Dictionary<string, (string Caller, string Callee)> _pendingCalls =
            new Dictionary<string, (string Caller, string Callee)>();        // برای ذخیره‌ی اطلاعات تماس در حال انتظار

        // ذخیره وضعیت فعلی هر تماس (بر اساس linkedId)
        private readonly Dictionary<string, string> _callStates = new Dictionary<string, string>();
        // نگاشت uniqueId به linkedId
        private readonly Dictionary<string, string> _uniqueIdToLinkedId = new Dictionary<string, string>();
        // تماس‌هایی که قبلاً کامل پردازش شده‌اند (برای جلوگیری از پردازش دوباره)
        private readonly Dictionary<string, DateTime> _hangedupCalls = new Dictionary<string, DateTime>();

        public AsteriskService(string host, int port, string username, string password, HashSet<string> internalNumbers, Action<string, string, bool> callHandler)
        {
            _manager = new ManagerConnection(host, port, username, password);
            _internalNumbers = internalNumbers;
            _manager.FireAllEvents = true;
            _manager.NewState += OnNewState;
            _manager.Hangup += OnHangup;
            _manager.NewExten += OnNewExtenEvent;

            try
            {
                // متصل به Asterisk AMI.
                _manager.Login();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                _manager.Logoff();
            }
            _callHandler = callHandler;
        }


        private void OnHangup(object sender, HangupEvent e)
        {

            if (!_uniqueIdToLinkedId.TryGetValue(e.UniqueId, out string linkedId) || string.IsNullOrEmpty(linkedId))
            {
                return;
            }

            //گاهی دوبار اتفاق میافتد

            if(_hangedupCalls.ContainsKey(linkedId))
            {
                return;
            }
            
            if (_pendingCalls.ContainsKey(linkedId))
            {
                // تماس از دست رفته
                var missed = _pendingCalls[linkedId];
                _callHandler?.Invoke(missed.Caller, missed.Callee, true);
                _pendingCalls.Remove(linkedId);
                _hangedupCalls[linkedId] = DateTime.Now.AddMinutes(10);
            }

            // پاک‌سازی کامل دیتاهای مربوط به تماس

            // حذف uniqueId های مرتبط به linkedId
            var uniqueIdsToRemove = new List<string>();
            foreach (var kvp in _uniqueIdToLinkedId)
            {
                if (kvp.Value == linkedId)
                    uniqueIdsToRemove.Add(kvp.Key);
            }
            foreach (var uniqueId in uniqueIdsToRemove)
            {
                _uniqueIdToLinkedId.Remove(uniqueId);
                _earlyStateEvents.Remove(uniqueId);
                _earlyStateEventTimes.Remove(uniqueId);
            }

            // حذف linkedId از دیکشنری ها و مجموعه های دیگه
            var removeHangups = new List<string>();
            var now = DateTime.Now;
            foreach (var hu in _hangedupCalls)
            {
                if(hu.Value < now)
                {
                    removeHangups.Add(hu.Key);
                }
            }
            foreach (var hu in removeHangups)
            {
                _hangedupCalls.Remove(hu);
            }
            _pendingCalls.Remove(linkedId);
        }
       
        private void OnNewExtenEvent(object sender, NewExtenEvent e)
        {
            string linkedId = e.Attributes?["linkedid"];
            if (!string.IsNullOrEmpty(e.UniqueId) && !string.IsNullOrEmpty(linkedId))
            {
                // نگاشت uniqueId به linkedId ثبت می‌شود
                _uniqueIdToLinkedId[e.UniqueId] = linkedId;

                // اگر قبلا رویداد NewState برای این uniqueId رسید، آن را همین‌جا پردازش کن
                //این رویداد همیشه پیش از رویداد NewState فراخوانی میشود. اما اگر برخلاف معمول پس از NewState فراخوانی شد باید کار انجام شود
                if (_earlyStateEvents.TryGetValue(e.UniqueId, out var earlyState))
                {
                    ProcessNewState(earlyState, linkedId);
                    _earlyStateEvents.Remove(e.UniqueId);
                    _earlyStateEventTimes.Remove(e.UniqueId);
                }
            }

            // فقط وقتی Application == "appdial" را پردازش می‌کنیم (مخصوص صف یا رینگ گروپ)
            if (string.IsNullOrEmpty(e.Application) || e.Application.ToLower() != "appdial")
                return;

            if (string.IsNullOrEmpty(linkedId))
                return;

            string callee = e.Attributes?["calleridnum"];
            string caller = e.Attributes?["connectedlinenum"];

            if (string.IsNullOrEmpty(caller) || string.IsNullOrEmpty(callee))
                return;

            // فقط تماس‌هایی که یکی از طرفین داخلی باشد را قبول کن
            if ((!_internalNumbers.Contains(caller) && !_internalNumbers.Contains(callee)) || (_internalNumbers.Contains(caller) && _internalNumbers.Contains(callee)))
                return;

            if (_pendingCalls.ContainsKey(linkedId))
                return;
            string state = e.Attributes["channelstatedesc"].ToString().ToLower();
            if (state == "ringing")
            {
                _pendingCalls[linkedId] = (caller, callee);
                _callHandler?.Invoke(caller, callee, false); // false یعنی تماس هنوز پاسخ داده نشده
            }
            if(state == "up")
            {
                _pendingCalls.Remove(linkedId);
            }
        }
        private void OnNewState(object sender, NewStateEvent e)
        {
            // چون ممکن است uniqueId هنوز در نگاشت نباشد، ابتدا سعی می‌کنیم linkedId را بگیریم
            if (!_uniqueIdToLinkedId.TryGetValue(e.UniqueId, out string linkedId) || string.IsNullOrEmpty(linkedId))
            {
                // اگر linkedId هنوز معلوم نیست، رویداد را موقتا ذخیره کن تا بعدا در OnNewExtenEvent پردازش شود
                _earlyStateEvents[e.UniqueId] = e;
                _earlyStateEventTimes[e.UniqueId] = DateTime.Now;
                CleanupEarlyEvents();
                return;
            }

            ProcessNewState(e, linkedId);
        }
        private void ProcessNewState(NewStateEvent e, string linkedId)
        {

            string newState = e.ChannelStateDesc?.ToLower() ?? "";

            // اگر وضعیت تغییر نکرده، کاری انجام نده
            if (_callStates.TryGetValue(linkedId, out string currentState) && currentState == newState)
                return;

            _callStates[linkedId] = newState;
            string callee = e.CallerIdNum;
            string caller = e.Connectedlinenum;

            if (string.IsNullOrEmpty(caller) || string.IsNullOrEmpty(callee))
                return;

            // فقط تماس‌هایی که یکی از طرفین داخلی باشد را قبول کن
            if ((!_internalNumbers.Contains(caller) && !_internalNumbers.Contains(callee)) || (_internalNumbers.Contains(caller) && _internalNumbers.Contains(callee)))
                return;

            string state = e.ChannelStateDesc?.ToLower() ?? "";

            switch (state)
            {
                case "ringing":
                    if (!_pendingCalls.ContainsKey(linkedId))
                    {
                        _pendingCalls[linkedId] = (caller, callee);
                        _callHandler?.Invoke(caller, callee, false);
                    }
                    break;

                case "up":
                    
                    _pendingCalls.Remove(linkedId);
                    break;
            }
        }

        private void CleanupEarlyEvents()
        {
            var now = DateTime.Now;
            var toRemove = new List<string>();

            foreach (var kvp in _earlyStateEventTimes)
            {
                if (now - kvp.Value > _earlyEventTimeout)
                    toRemove.Add(kvp.Key);
            }

            foreach (var uniqueId in toRemove)
            {
                _earlyStateEvents.Remove(uniqueId);
                _earlyStateEventTimes.Remove(uniqueId);
            }
        }

        public (bool IsSuccess, string Message) ClickToCall(string internalNumber, string destNumber, string callerID = "1000")
        {
            //var originateAction = new OriginateAction
            //{
            //    Channel = $"SIP/{internalNumber}",         // داخلی اپراتور
            //    Context = "from-internal",    // کانتکست در dialplan
            //    Exten = destNumber,        // شماره مشتری یا مقصد
            //    Priority = "1",
            //    CallerId = internalNumber,// destNumber,//callerID,
            //    Timeout = 30000               // در میلی‌ثانیه
            //};
            if (string.IsNullOrWhiteSpace(internalNumber))
            {
                return (false, "داخلی تماس گیرنده معتبر نیست. شماره داخلی کاربر کنونی را در صفحه مدیریت اطلاعات کاربران بررسی کنید.");
            }
            var originateAction = new OriginateAction()
            {
                Channel = $"Local/{internalNumber}@from-internal",  // انگار تماس از اپراتوره
                Context = "from-internal",
                Exten = destNumber,       // شماره مشتری
                Priority = "1",
                CallerId = destNumber,  // نمایش شماره اپراتور
                Timeout = 30000
            };
            var response = _manager.SendAction(originateAction, 30000);
            string message = response.Message;
            if (message == "")
            {
                message = "";
            }
            return (response.IsSuccess(), message);
        }
        public Dictionary<string, string> GetExtensionState()
        {
            Dictionary<string, string> result = new Dictionary<string, string>();
            // 1. دریافت لیست وضعیت SIP
            var action = new CommandAction("sip show peers");
            CommandResponse sipPeers = (CommandResponse)_manager.SendAction(action);

            // 2. دریافت لیست کانال‌های فعال
            var chanAction = new CommandAction("core show channels");
            CommandResponse activeCalls = (CommandResponse)_manager.SendAction(chanAction);
            var sipLines = sipPeers.Result;
            var activeLines = activeCalls.Result;
            foreach (string ext in _internalNumbers)
            {
                string extStr = ext.ToString();
                string line = sipLines.FirstOrDefault(l => l.StartsWith($"{extStr}/"));

                string status = "نامشخص";

                if (line != null)
                {
                    if (line.Contains("UNREACHABLE"))
                        status = "داخلی در دسترس نیست (Unreachable)";
                    else if (line.Contains("OK"))
                        status = "داخلی ثبت شده (Available)";
                    else
                        status = "ثبت نشده (Not Registered)";
                }
                else
                {
                    status = "یافت نشد";
                }

                // آیا داخلی در تماس هست؟
                bool inCall = activeLines.Any(l => l.Contains($"SIP/{extStr}"));

                if (inCall)
                    status += " + در حال تماس";

                result.Add(extStr, status);
            }
            return result;
        }



        public void Stop()
        {
            if (_manager != null && _manager.IsConnected())
            {
                _manager.Logoff();
                Console.WriteLine("Disconnected from Asterisk AMI.");
            }
        }


            }
}
