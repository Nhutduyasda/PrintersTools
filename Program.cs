using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace PrinterFixTool
{
    internal class Program
    {
        // Thống kê kết quả thực thi
        private static int _totalPrintersScanned = 0;
        private static int _offlineFixedCount = 0;
        private static int _networkPrintersCount = 0;
        private static int _spoolFilesRemoved = 0;
        private static bool _spoolerRestartedSuccessfully = false;
        private static bool _registryPatchedSuccessfully = false;

        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            PrintBanner();

            // 0. Kiểm tra quyền Administrator
            CheckAdminPrivileges();

            // Module 1: Quét WMI, kiểm tra trạng thái máy in, tự động gỡ Offline & Ping kiểm tra mạng
            RunModule1_PrinterScanAndFix();

            // Module 2: Làm sạch Print Spooler (xóa lệnh in kẹt .spl, .shd, .tmp)
            RunModule2_CleanPrintSpooler();

            // Module 3: Vá lỗi Registry chia sẻ máy in mạng LAN (0x0000011b & 0x00000709)
            RunModule3_PatchRegistry();

            // Bảng tóm tắt kết quả
            PrintSummary();

            Console.WriteLine();
            PrintColored("[HOÀN TẤT] Nhấn phím bất kỳ để đóng công cụ...", ConsoleColor.Cyan);
            if (!Console.IsInputRedirected)
            {
                try
                {
                    Console.ReadKey(intercept: true);
                }
                catch
                {
                    // Tránh lỗi khi chạy trong môi trường không có Interactive Console
                }
            }
        }

        #region UI & Helpers

        private static void PrintBanner()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("================================================================================");
            Console.WriteLine("          CÔNG CỤ TỰ ĐỘNG CHẨN ĐOÁN & SỬA LỖI MÁY IN WINDOWS (.NET 8)          ");
            Console.WriteLine("                      Senior Systems Engineering Tool                          ");
            Console.WriteLine("================================================================================");
            Console.ResetColor();
            Console.WriteLine();
        }

        private static void PrintSectionHeader(string title)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[+] ------------------ {title} ------------------");
            Console.ResetColor();
        }

        private static void PrintColored(string message, ConsoleColor color)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            Console.ResetColor();
        }

        private static void CheckAdminPrivileges()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                bool isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);

                if (isAdmin)
                {
                    PrintColored("[OK] Đang chạy với quyền Administrator (Toàn quyền hệ thống).", ConsoleColor.Green);
                }
                else
                {
                    PrintColored("[CẢNH BÁO] Ứng dụng KHÔNG chạy với quyền Administrator! Một số thao tác như dừng dịch vụ Spooler hoặc sửa Registry có thể bị từ chối truy cập.", ConsoleColor.Red);
                }
            }
            catch (Exception ex)
            {
                PrintColored($"[LƯU Ý] Không thể kiểm tra quyền hạn chi tiết: {ex.Message}", ConsoleColor.Yellow);
            }
        }

        #endregion

        #region Module 1: Quét WMI & Tự động gỡ Offline & Ping kiểm tra mạng

        private static void RunModule1_PrinterScanAndFix()
        {
            PrintSectionHeader("MODULE 1: QUÉT WMI, TỰ ĐỘNG GỠ OFFLINE & KIỂM TRA MẠNG MÁY IN");

            // 1.1 Lấy map TCP/IP Printer Port (Port Name -> HostAddress)
            PrintColored("[*] Đang truy vấn danh sách cổng TCP/IP từ Win32_TCPIPPrinterPort...", ConsoleColor.Yellow);
            var tcpPortMap = GetTcpIpPrinterPorts();
            PrintColored($"[*] Đã phát hiện {tcpPortMap.Count} cổng TCP/IP được cấu hình.", ConsoleColor.Gray);

            // 1.2 Danh sách lọc máy in ảo mặc định
            var virtualPrinterKeywords = new[]
            {
                "onenote",
                "microsoft print to pdf",
                "xps",
                "fax"
            };

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Printer");
                using var results = searcher.Get();

                foreach (ManagementObject printer in results)
                {
                    string printerName = printer["Name"]?.ToString() ?? "Unknown Printer";
                    string portName = printer["PortName"]?.ToString() ?? "";
                    string driverName = printer["DriverName"]?.ToString() ?? "";

                    // Kiểm tra và lọc bỏ máy in ảo
                    bool isVirtual = false;
                    foreach (var kw in virtualPrinterKeywords)
                    {
                        if (printerName.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                            driverName.Contains(kw, StringComparison.OrdinalIgnoreCase))
                        {
                            isVirtual = true;
                            break;
                        }
                    }

                    if (isVirtual)
                    {
                        continue;
                    }

                    _totalPrintersScanned++;
                    Console.WriteLine();
                    PrintColored($"----------------------------------------------------------------------", ConsoleColor.DarkGray);
                    PrintColored($"[MÁY IN #{_totalPrintersScanned}] {printerName}", ConsoleColor.White);
                    PrintColored($"  - Cổng (Port): {portName}", ConsoleColor.Gray);
                    PrintColored($"  - Driver    : {driverName}", ConsoleColor.Gray);

                    // 1.3 Kiểm tra WorkOffline và tự động ép Online nếu bị Offline
                    bool isOffline = false;
                    if (printer["WorkOffline"] is bool offlineVal)
                    {
                        isOffline = offlineVal;
                    }
                    else if (bool.TryParse(printer["WorkOffline"]?.ToString(), out bool parsedOffline))
                    {
                        isOffline = parsedOffline;
                    }

                    if (isOffline)
                    {
                        PrintColored("  - Trạng thái WorkOffline: TRUE [Đang bị Offline]", ConsoleColor.Yellow);
                        PrintColored("  -> Đang thực hiện gỡ bỏ cờ Offline qua WMI...", ConsoleColor.Yellow);
                        try
                        {
                            printer["WorkOffline"] = false;
                            printer.Put();
                            _offlineFixedCount++;
                            PrintColored("  -> [THÀNH CÔNG] Đã chuyển máy in về trạng thái ONLINE!", ConsoleColor.Green);
                        }
                        catch (Exception ex)
                        {
                            PrintColored($"  -> [LỖI] Không thể gỡ Offline qua WMI: {ex.Message}", ConsoleColor.Red);
                        }
                    }
                    else
                    {
                        PrintColored("  - Trạng thái WorkOffline: FALSE [Online]", ConsoleColor.Green);
                    }

                    // 1.4 Kiểm tra lỗi vật lý (PrinterStatus, ExtendedPrinterStatus, DetectedErrorState)
                    CheckPhysicalPrinterErrors(printer);

                    // 1.5 Xác định địa chỉ mạng và Ping kiểm tra kết nối vật lý
                    CheckPrinterNetworkConnectivity(portName, tcpPortMap);
                }

                if (_totalPrintersScanned == 0)
                {
                    PrintColored("[THÔNG BÁO] Không tìm thấy máy in vật lý nào (chỉ có máy in ảo hoặc chưa cài máy in).", ConsoleColor.Yellow);
                }
            }
            catch (Exception ex)
            {
                PrintColored($"[LỖI WMI] Lỗi khi truy vấn Win32_Printer: {ex.Message}", ConsoleColor.Red);
            }
        }

        private static Dictionary<string, string> GetTcpIpPrinterPorts()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, HostAddress FROM Win32_TCPIPPrinterPort");
                using var collection = searcher.Get();
                foreach (ManagementObject port in collection)
                {
                    string? name = port["Name"]?.ToString();
                    string? host = port["HostAddress"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(host))
                    {
                        map[name.Trim()] = host.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                PrintColored($"[LƯU Ý] Không thể đọc danh sách Win32_TCPIPPrinterPort: {ex.Message}", ConsoleColor.DarkYellow);
            }
            return map;
        }

        private static void CheckPhysicalPrinterErrors(ManagementObject printer)
        {
            // Trích xuất các mã trạng thái
            ushort printerStatus = 0;
            ushort extPrinterStatus = 0;
            ushort detectedErrorState = 0;

            if (printer["PrinterStatus"] != null)
                ushort.TryParse(printer["PrinterStatus"].ToString(), out printerStatus);

            if (printer["ExtendedPrinterStatus"] != null)
                ushort.TryParse(printer["ExtendedPrinterStatus"].ToString(), out extPrinterStatus);

            if (printer["DetectedErrorState"] != null)
                ushort.TryParse(printer["DetectedErrorState"].ToString(), out detectedErrorState);
            else if (printer["ExtendedDetectedErrorState"] != null)
                ushort.TryParse(printer["ExtendedDetectedErrorState"].ToString(), out detectedErrorState);

            var physicalErrors = new List<string>();

            // Phân tích DetectedErrorState
            switch (detectedErrorState)
            {
                case 3:
                    physicalErrors.Add("Sắp hết giấy (Low Paper)");
                    break;
                case 4:
                    physicalErrors.Add("HẾT GIẤY (No Paper / Out of paper)");
                    break;
                case 5:
                    physicalErrors.Add("Sắp hết mực (Low Toner)");
                    break;
                case 6:
                    physicalErrors.Add("HẾT MỰC (No Toner)");
                    break;
                case 7:
                    physicalErrors.Add("NẮP MÁY IN ĐANG MỞ (Door Open)");
                    break;
                case 8:
                    physicalErrors.Add("KẸT GIẤY (Paper Jam)");
                    break;
                case 9:
                    physicalErrors.Add("Cần bảo dưỡng kỹ thuật (Service Requested)");
                    break;
                case 10:
                    physicalErrors.Add("Khay giấy ra bị đầy (Output Bin Full)");
                    break;
                case 11:
                    physicalErrors.Add("Trục trặc nạp giấy (Paper Problem)");
                    break;
                case 13:
                    physicalErrors.Add("Yêu cầu người dùng can thiệp (User Intervention Required)");
                    break;
            }

            // Phân tích ExtendedPrinterStatus
            if (extPrinterStatus == 9) // Error
            {
                physicalErrors.Add("Máy in đang báo lỗi chung (Error Status)");
            }
            else if (extPrinterStatus == 11) // Not Available
            {
                physicalErrors.Add("Máy in tạm thời không sẵn sàng (Not Available)");
            }

            if (physicalErrors.Count > 0)
            {
                PrintColored("  -> [CẢNH BÁO LỖI VẬT LÝ]:", ConsoleColor.Red);
                foreach (var err in physicalErrors)
                {
                    PrintColored($"     ! {err}", ConsoleColor.Red);
                }
                PrintColored("     => Vui lòng kiểm tra khay giấy, gỡ giấy kẹt hoặc đóng chặt nắp máy in.", ConsoleColor.Yellow);
            }
            else
            {
                PrintColored("  - Cảm biến phần cứng: Bình thường (Không phát hiện kẹt giấy, hết giấy, mở nắp).", ConsoleColor.Green);
            }
        }

        private static void CheckPrinterNetworkConnectivity(string portName, Dictionary<string, string> tcpPortMap)
        {
            string? targetHost = null;
            string connectionType = "";

            if (string.IsNullOrWhiteSpace(portName))
            {
                PrintColored("  - Không xác định được cổng máy in.", ConsoleColor.DarkGray);
                return;
            }

            // Trường hợp 1: Máy in mạng LAN Share UNC (bắt đầu bằng \\)
            if (portName.StartsWith(@"\\"))
            {
                var parts = portName.TrimStart('\\').Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    targetHost = parts[0];
                    connectionType = $"Máy in mạng LAN chia sẻ (UNC Server: {targetHost})";
                }
            }
            // Trường hợp 2: Có trong Win32_TCPIPPrinterPort
            else if (tcpPortMap.TryGetValue(portName, out var mappedHost))
            {
                targetHost = mappedHost;
                connectionType = $"Cổng mạng TCP/IP tiêu chuẩn (IP: {targetHost})";
            }
            // Trường hợp 3: Port Name có dạng IP_xxx.xxx.xxx.xxx
            else if (portName.StartsWith("IP_", StringComparison.OrdinalIgnoreCase))
            {
                string ipCandidate = portName.Substring(3).Trim();
                targetHost = ipCandidate;
                connectionType = $"Cổng TCP/IP qua tên cổng (IP: {targetHost})";
            }
            // Trường hợp 4: Port Name là địa chỉ IP thuần
            else if (Regex.IsMatch(portName, @"^(\d{1,3}\.){3}\d{1,3}$"))
            {
                targetHost = portName;
                connectionType = $"Cổng mạng IP trực tiếp ({targetHost})";
            }
            // Trường hợp 5: Cổng cục bộ (USB, LPT, COM, WSD, v.v.)
            else
            {
                PrintColored($"  - Kiểu kết nối: Cổng cục bộ/Trực tiếp [{portName}] (Không cần Ping kiểm tra mạng)", ConsoleColor.Gray);
                return;
            }

            if (!string.IsNullOrWhiteSpace(targetHost))
            {
                _networkPrintersCount++;
                PrintColored($"  - Kiểu kết nối: {connectionType}", ConsoleColor.Cyan);
                PrintColored($"  -> Đang gửi gói tin Ping kiểm tra đến {targetHost} (Timeout: 2000ms)...", ConsoleColor.Yellow);

                try
                {
                    using var ping = new Ping();
                    var reply = ping.Send(targetHost, 2000);

                    if (reply.Status == IPStatus.Success)
                    {
                        PrintColored($"  -> [Mạng OK] Ping tới {targetHost} thành công (Thời gian phản hồi: {reply.RoundtripTime} ms).", ConsoleColor.Green);
                    }
                    else
                    {
                        PrintNetworkFailure(targetHost, reply.Status.ToString());
                    }
                }
                catch (Exception ex)
                {
                    PrintNetworkFailure(targetHost, ex.InnerException?.Message ?? ex.Message);
                }
            }
        }

        private static void PrintNetworkFailure(string targetHost, string reason)
        {
            PrintColored($"  -> [MẤT KẾT NỐI VẬT LÝ] Không thể Ping tới {targetHost}! (Chi tiết: {reason})", ConsoleColor.Red);
            PrintColored("     [HƯỚNG DẪN KHẮC PHỤC SỰ CỐ MẠNG]:", ConsoleColor.Yellow);
            PrintColored("     1. Kiểm tra nguồn điện máy in: Đảm bảo máy in đang bật đèn xanh và không ở chế độ Sleep sâu.", ConsoleColor.Yellow);
            PrintColored("     2. Kiểm tra dây cáp mạng LAN hoặc kết nối Wi-Fi trên máy in.", ConsoleColor.Yellow);
            PrintColored("     3. Nếu là máy in chia sẻ qua máy tính khác (UNC): Kiểm tra máy chủ có đang bật và thông mạng không.", ConsoleColor.Yellow);
            PrintColored("     4. Kiểm tra địa chỉ IP máy in: Có thể IP đã bị thay đổi do Router cấp phát lại DHCP. Hãy in bản cấu hình mạng trên máy in để kiểm tra IP hiện tại.", ConsoleColor.Yellow);
        }

        #endregion

        #region Module 2: Làm sạch Print Spooler

        private static void RunModule2_CleanPrintSpooler()
        {
            PrintSectionHeader("MODULE 2: LÀM SẠCH PRINT SPOOLER & XÓA LỆNH IN KẸT");

            PrintColored("[*] Đang kết nối dịch vụ Print Spooler (spoolsv.exe)...", ConsoleColor.Yellow);

            try
            {
                using var sc = new ServiceController("Spooler");

                // Bước 1: Dừng dịch vụ Print Spooler
                StopSpoolerService(sc);

                // Bước 2: Xóa toàn bộ file kẹt trong thư mục Spool PRINTERS
                ClearSpoolerPrintersFolder();

                // Bước 3: Khởi động lại dịch vụ Print Spooler
                StartSpoolerService(sc);
            }
            catch (Exception ex)
            {
                PrintColored($"[LỖI SPOOLER] Thao tác với dịch vụ Spooler thất bại: {ex.Message}", ConsoleColor.Red);
            }
        }

        private static void StopSpoolerService(ServiceController sc)
        {
            try
            {
                sc.Refresh();
                if (sc.Status == ServiceControllerStatus.Stopped)
                {
                    PrintColored("[*] Dịch vụ Spooler đã ở trạng thái dừng từ trước.", ConsoleColor.Gray);
                    return;
                }

                PrintColored("[*] Đang gửi yêu cầu dừng dịch vụ Spooler (Chờ tối đa 10s)...", ConsoleColor.Yellow);
                if (sc.CanStop)
                {
                    sc.Stop();
                }

                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                PrintColored("[THÀNH CÔNG] Dịch vụ Spooler đã dừng an toàn.", ConsoleColor.Green);
            }
            catch (Exception ex)
            {
                PrintColored($"[CẢNH BÁO] Không thể dừng Spooler êm dịu ({ex.Message}). Đang cưỡng chế dừng bằng taskkill...", ConsoleColor.Yellow);
                ForceKillSpooler();
            }

            // Đảm bảo Spooler thực sự dừng
            Thread.Sleep(1000);
            sc.Refresh();
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                ForceKillSpooler();
            }
        }

        private static void ForceKillSpooler()
        {
            try
            {
                var psi = new ProcessStartInfo("taskkill", "/F /IM spoolsv.exe")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var p = Process.Start(psi);
                if (p != null)
                {
                    p.WaitForExit(5000);
                    PrintColored("[OK] Đã thực thi lệnh taskkill /F /IM spoolsv.exe thành công.", ConsoleColor.Green);
                }
            }
            catch (Exception ex)
            {
                PrintColored($"[LỖI TASKKILL] Không thể cưỡng chế kill spoolsv.exe: {ex.Message}", ConsoleColor.Red);
            }
        }

        private static void ClearSpoolerPrintersFolder()
        {
            string systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string spoolPath = Path.Combine(systemRoot, "System32", "spool", "PRINTERS");

            PrintColored($"[*] Đang kiểm tra thư mục lưu tạm lệnh in: {spoolPath}", ConsoleColor.Yellow);

            if (!Directory.Exists(spoolPath))
            {
                PrintColored("[LƯU Ý] Thư mục spool\\PRINTERS không tồn tại, tự động tạo mới...", ConsoleColor.DarkYellow);
                try
                {
                    Directory.CreateDirectory(spoolPath);
                }
                catch (Exception ex)
                {
                    PrintColored($"[LỖI] Không thể tạo thư mục PRINTERS: {ex.Message}", ConsoleColor.Red);
                    return;
                }
            }

            string[] searchPatterns = { "*.spl", "*.shd", "*.tmp" };
            var filesToDelete = new List<FileInfo>();

            try
            {
                var dirInfo = new DirectoryInfo(spoolPath);
                foreach (var pattern in searchPatterns)
                {
                    filesToDelete.AddRange(dirInfo.GetFiles(pattern));
                }

                if (filesToDelete.Count == 0)
                {
                    PrintColored("[OK] Hàng đợi sạch sẽ: Không có file lệnh in nào bị kẹt (*.spl, *.shd, *.tmp).", ConsoleColor.Green);
                    return;
                }

                PrintColored($"[*] Phát hiện {filesToDelete.Count} file lệnh in đang bị kẹt hoặc tồn đọng. Đang tiến hành xóa...", ConsoleColor.Yellow);

                int successCount = 0;
                int failCount = 0;

                foreach (var file in filesToDelete)
                {
                    try
                    {
                        // Gỡ bỏ thuộc tính ReadOnly nếu có
                        if (file.IsReadOnly)
                        {
                            file.IsReadOnly = false;
                        }

                        file.Delete();
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        PrintColored($"  ! Bỏ qua file bị khóa {file.Name}: {ex.Message}", ConsoleColor.DarkYellow);
                    }
                }

                _spoolFilesRemoved = successCount;
                PrintColored($"[THÀNH CÔNG] Đã xóa thành công {successCount}/{filesToDelete.Count} file lệnh in bị kẹt.", ConsoleColor.Green);
                if (failCount > 0)
                {
                    PrintColored($"[CẢNH BÁO] Có {failCount} file không thể xóa do bị khóa bởi tiến trình khác.", ConsoleColor.Yellow);
                }
            }
            catch (Exception ex)
            {
                PrintColored($"[LỖI] Không thể quét thư mục spool\\PRINTERS: {ex.Message}", ConsoleColor.Red);
            }
        }

        private static void StartSpoolerService(ServiceController sc)
        {
            PrintColored("[*] Đang khởi động lại dịch vụ Print Spooler...", ConsoleColor.Yellow);
            try
            {
                sc.Refresh();
                if (sc.Status != ServiceControllerStatus.Running)
                {
                    sc.Start();
                }

                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                sc.Refresh();

                if (sc.Status == ServiceControllerStatus.Running)
                {
                    _spoolerRestartedSuccessfully = true;
                    PrintColored("[THÀNH CÔNG] Dịch vụ Print Spooler đã hoạt động bình thường (Status: Running)!", ConsoleColor.Green);
                }
                else
                {
                    PrintColored($"[LỖI] Trạng thái dịch vụ Spooler hiện tại: {sc.Status}", ConsoleColor.Red);
                }
            }
            catch (Exception ex)
            {
                PrintColored($"[LỖI KHỞI ĐỘNG SPOOLER] Không thể khởi động Spooler: {ex.Message}", ConsoleColor.Red);
            }
        }

        #endregion

        #region Module 3: Vá lỗi Registry chia sẻ máy in mạng LAN (0x0000011b & 0x00000709)

        private static void RunModule3_PatchRegistry()
        {
            PrintSectionHeader("MODULE 3: VÁ LỖI REGISTRY CHIA SẺ MÁY IN MẠNG LAN (WINDOWS 10/11)");

            bool patch1Ok = false;
            bool patch2Ok = false;

            // 3.1 Sửa lỗi 0x0000011b (RpcAuthnLevelPrivacyEnabled = 0 tại HKLM\System\CurrentControlSet\Control\Print)
            PrintColored("[*] Đang kiểm tra & cấu hình vá lỗi 0x0000011b (RpcAuthnLevelPrivacyEnabled)...", ConsoleColor.Yellow);
            try
            {
                const string printKeyPath = @"System\CurrentControlSet\Control\Print";
                using var printKey = Registry.LocalMachine.OpenSubKey(printKeyPath, writable: true);
                if (printKey != null)
                {
                    printKey.SetValue("RpcAuthnLevelPrivacyEnabled", 0, RegistryValueKind.DWord);
                    PrintColored(@"  -> [THÀNH CÔNG] Đã thiết lập HKLM\System\CurrentControlSet\Control\Print\RpcAuthnLevelPrivacyEnabled = 0 (DWORD)", ConsoleColor.Green);
                    PrintColored("     (Giúp khắc phục hoàn toàn lỗi chia sẻ máy in mã 0x0000011b sau các bản cập nhật bảo mật RPC).", ConsoleColor.Gray);
                    patch1Ok = true;
                }
                else
                {
                    PrintColored(@"  -> [LỖI] Không thể mở subkey HKLM\System\CurrentControlSet\Control\Print với quyền ghi.", ConsoleColor.Red);
                }
            }
            catch (UnauthorizedAccessException)
            {
                PrintColored(@"  -> [TỪ CHỐI TRUY CẬP] Cần quyền Administrator để chỉnh sửa Registry HKLM!", ConsoleColor.Red);
            }
            catch (Exception ex)
            {
                PrintColored($"  -> [LỖI] Sửa lỗi 0x0000011b thất bại: {ex.Message}", ConsoleColor.Red);
            }

            // 3.2 Sửa lỗi 0x00000709 trên Win 11 22H2+ (RpcUseNamedPipeProtocol = 1 tại HKLM\Software\Policies\Microsoft\Windows NT\Printers\RPC)
            PrintColored("[*] Đang kiểm tra & cấu hình vá lỗi 0x00000709 (RpcUseNamedPipeProtocol)...", ConsoleColor.Yellow);
            try
            {
                const string rpcKeyPath = @"Software\Policies\Microsoft\Windows NT\Printers\RPC";
                using var rpcKey = Registry.LocalMachine.CreateSubKey(rpcKeyPath, writable: true);
                if (rpcKey != null)
                {
                    rpcKey.SetValue("RpcUseNamedPipeProtocol", 1, RegistryValueKind.DWord);
                    PrintColored(@"  -> [THÀNH CÔNG] Đã thiết lập HKLM\Software\Policies\Microsoft\Windows NT\Printers\RPC\RpcUseNamedPipeProtocol = 1 (DWORD)", ConsoleColor.Green);
                    PrintColored("     (Khắc phục lỗi kết nối máy in 0x00000709 khi sử dụng Named Pipe trên Windows 11 22H2+).", ConsoleColor.Gray);
                    patch2Ok = true;
                }
                else
                {
                    PrintColored(@"  -> [LỖI] Không thể tạo/mở subkey HKLM\Software\Policies\Microsoft\Windows NT\Printers\RPC", ConsoleColor.Red);
                }
            }
            catch (UnauthorizedAccessException)
            {
                PrintColored(@"  -> [TỪ CHỐI TRUY CẬP] Cần quyền Administrator để chỉnh sửa Registry HKLM!", ConsoleColor.Red);
            }
            catch (Exception ex)
            {
                PrintColored($"  -> [LỖI] Sửa lỗi 0x00000709 thất bại: {ex.Message}", ConsoleColor.Red);
            }

            _registryPatchedSuccessfully = patch1Ok && patch2Ok;
        }

        #endregion

        #region Bảng tóm tắt kết quả

        private static void PrintSummary()
        {
            PrintSectionHeader("BẢNG TỔNG HỢP KẾT QUẢ XỬ LÝ");

            Console.WriteLine($"┌─────────────────────────────────────────────────────────────┬───────────────────────────┐");
            Console.WriteLine($"│ Hạng mục kiểm tra & khắc phục                               │ Trạng thái                │");
            Console.WriteLine($"├─────────────────────────────────────────────────────────────┼───────────────────────────┤");
            Console.WriteLine($"│ 1. Tổng số máy in vật lý được quét                          │ {PadCenter(_totalPrintersScanned.ToString(), 25)} │");
            Console.WriteLine($"│ 2. Số máy in tự động gỡ Offline -> Online                   │ {PadCenter(_offlineFixedCount.ToString(), 25)} │");
            Console.WriteLine($"│ 3. Số máy in mạng (TCP/IP & UNC LAN) đã kiểm tra            │ {PadCenter(_networkPrintersCount.ToString(), 25)} │");
            Console.WriteLine($"│ 4. Số file lệnh in kẹt đã dọn sạch (*.spl, *.shd, *.tmp)    │ {PadCenter(_spoolFilesRemoved.ToString(), 25)} │");

            string spoolerStatus = _spoolerRestartedSuccessfully ? "Đang chạy (Running) [OK]" : "Lỗi / Chưa chạy";
            Console.WriteLine($"│ 5. Trạng thái dịch vụ Print Spooler                         │ {PadCenter(spoolerStatus, 25)} │");

            string regStatus = _registryPatchedSuccessfully ? "Đã vá thành công [OK]" : "Chưa hoàn tất";
            Console.WriteLine($"│ 6. Vá Registry mạng LAN (0x0000011b & 0x00000709)           │ {PadCenter(regStatus, 25)} │");
            Console.WriteLine($"└─────────────────────────────────────────────────────────────┴───────────────────────────┘");
        }

        private static string PadCenter(string text, int width)
        {
            if (text.Length >= width) return text.Substring(0, width);
            int leftPad = (width - text.Length) / 2;
            int rightPad = width - text.Length - leftPad;
            return new string(' ', leftPad) + text + new string(' ', rightPad);
        }

        #endregion
    }
}
